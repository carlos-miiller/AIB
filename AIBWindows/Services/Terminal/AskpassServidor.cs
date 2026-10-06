using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AIB.Services.Terminal;

/// <summary>O que o prompt quer: uma senha (sem eco), um texto visível, ou sim/não.</summary>
public enum TipoDePedido { Senha, Texto, SimNao }

/// <summary>
/// Um prompt que um programa rodando no shell da AIB fez ao usuário.
/// </summary>
/// <param name="Prompt">O texto exato que o programa mandou (ex.: "carlo@srv's password: ").</param>
/// <param name="Comando">O comando do shell dentro do qual o pedido nasceu.</param>
/// <param name="Solicitante">Caminho do executável que pediu (o pai do askpass), ou null.</param>
/// <param name="SolicitanteConfiavel">Se o executável mora numa pasta protegida (OpenSSH do
/// Windows, Git em Program Files). Um pedido de outro lugar pode ser alguém fingindo ser o ssh
/// para que o que for digitado volte para a IA.</param>
public record PedidoDeSenha(
    string Prompt,
    string Comando,
    string? Solicitante,
    bool SolicitanteConfiavel,
    TipoDePedido Tipo);

/// <summary>A resposta do usuário. <c>Texto</c> null é cancelar.</summary>
public record RespostaDoUsuario(string? Texto, bool Lembrar);

/// <summary>
/// Faz o prompt de senha de um programa do shell chegar ao USUÁRIO, e não à IA.
/// <para>
/// O shell da AIB roda sem console e com a entrada fechada (ver <c>RunCommandTool</c>): um
/// <c>ssh</c> que pede senha não teria onde perguntar. O OpenSSH e o git aceitam um programa
/// "askpass" para isso (<c>SSH_ASKPASS</c> + <c>SSH_ASKPASS_REQUIRE=force</c>,
/// <c>GIT_ASKPASS</c>). O askpass é o próprio AIB.exe (ver <see cref="AskpassCliente"/>), que
/// repassa o prompt por um pipe nomeado até aqui; aqui a janela abre, o usuário digita, e a
/// resposta volta pelo mesmo caminho até a saída padrão do askpass — que o ssh lê.
/// </para>
/// <para>
/// A senha nunca passa pelo modelo, pelo histórico nem pelo disco: ela vai da janela para o
/// pipe e do pipe para o ssh.
/// </para>
/// <para>
/// Só atende quem traz um token vivo. O token nasce com cada comando do shell
/// (<see cref="AbrirSessao"/>), vai no ambiente dele, e morre quando o comando termina. Um
/// processo qualquer da máquina que ache o pipe não tem o token.
/// </para>
/// </summary>
public sealed class AskpassServidor : IDisposable
{
    public const string VarPipe = "AIB_ASKPASS_PIPE";
    public const string VarToken = "AIB_ASKPASS_TOKEN";

    /// <summary>A instância do app, ligada no arranque. Null nos ensaios e antes do arranque.</summary>
    public static AskpassServidor? Padrao { get; set; }

    private readonly Func<PedidoDeSenha, Task<RespostaDoUsuario?>> _perguntar;
    private readonly Func<int, string?> _quemPede;
    private readonly Dictionary<string, Sessao> _sessoes = new();

    // Senhas lembradas "até fechar o AIB", pela chave do prompt exato. O prompt do ssh já traz
    // usuário@host, então a senha de um servidor nunca é oferecida a outro.
    private readonly Dictionary<string, string> _lembradas = new(StringComparer.Ordinal);
    private readonly object _trava = new();

    // Uma janela de cada vez. Dois comandos pedindo juntos abririam duas janelas de senha
    // empilhadas, e o usuário digitaria a senha de um no outro.
    private readonly SemaphoreSlim _umaJanela = new(1, 1);

    private readonly CancellationTokenSource _fim = new();
    private Task? _escuta;

    public string NomeDoPipe { get; }

    /// <param name="perguntar">Mostra o pedido ao usuário. Null na resposta é cancelar.</param>
    /// <param name="quemPede">Do PID do askpass, o caminho do executável que o chamou.</param>
    public AskpassServidor(
        Func<PedidoDeSenha, Task<RespostaDoUsuario?>> perguntar,
        Func<int, string?>? quemPede = null)
    {
        _perguntar = perguntar;
        _quemPede = quemPede ?? CaminhoDoPai;
        NomeDoPipe = $"AIB.askpass.{Environment.ProcessId}.{Convert.ToHexString(RandomNumberGenerator.GetBytes(6))}";
    }

    // ── Sessões ─────────────────────────────────────────────────────────

    /// <summary>
    /// A janela de validade de um token: o tempo de vida de um comando do shell.
    /// </summary>
    public sealed class Sessao : IDisposable
    {
        private readonly AskpassServidor _dono;
        private readonly Stopwatch _relogio = new();
        private TimeSpan _acumulado;
        private int _janelasAbertas;

        // Prompts que esta sessão respondeu com a senha lembrada. Se o mesmo prompt voltar, a
        // senha lembrada estava errada: o ssh pergunta de novo, e responder o mesmo três vezes
        // só gastaria as tentativas do servidor.
        internal readonly HashSet<string> RespondidosDaMemoria = new(StringComparer.Ordinal);

        public string Token { get; }
        public string Comando { get; }

        internal Sessao(AskpassServidor dono, string comando)
        {
            _dono = dono;
            Comando = comando;
            Token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        }

        /// <summary>
        /// Quanto tempo o comando passou esperando o usuário digitar. O teto do shell desconta
        /// isto: 30 s para o comando, não para a pessoa achar a senha.
        /// </summary>
        public TimeSpan TempoComUsuario
        {
            get { lock (_relogio) return _acumulado + (_janelasAbertas > 0 ? _relogio.Elapsed : TimeSpan.Zero); }
        }

        internal void ComecouAEsperar()
        {
            lock (_relogio) { if (_janelasAbertas++ == 0) _relogio.Restart(); }
        }

        internal void ParouDeEsperar()
        {
            lock (_relogio)
            {
                if (--_janelasAbertas == 0) { _acumulado += _relogio.Elapsed; _relogio.Reset(); }
            }
        }

        public void Dispose() => _dono.Fechar(this);
    }

    /// <summary>
    /// Abre a janela de validade de um comando. O token vai no ambiente do processo
    /// (<see cref="Preparar"/>) e deixa de valer no <c>Dispose</c>.
    /// </summary>
    public Sessao AbrirSessao(string comando)
    {
        var sessao = new Sessao(this, comando);
        lock (_trava)
        {
            _sessoes[sessao.Token] = sessao;
            _escuta ??= Task.Run(() => EscutarAsync(_fim.Token));
        }
        return sessao;
    }

    private void Fechar(Sessao sessao)
    {
        lock (_trava) _sessoes.Remove(sessao.Token);
    }

    /// <summary>
    /// Põe no ambiente do processo o que faz ssh, scp, sftp e git perguntarem por aqui.
    /// </summary>
    public void Preparar(ProcessStartInfo inicio, Sessao sessao)
    {
        string exe = Environment.ProcessPath ?? "";
        if (exe.Length == 0) return;

        inicio.Environment["SSH_ASKPASS"] = exe;
        // "force": sem isto o ssh só usa o askpass quando há DISPLAY e não há terminal — regra
        // do X11, que no Windows nunca vale.
        inicio.Environment["SSH_ASKPASS_REQUIRE"] = "force";
        inicio.Environment["GIT_ASKPASS"] = exe;
        inicio.Environment[VarPipe] = NomeDoPipe;
        inicio.Environment[VarToken] = sessao.Token;
    }

    // ── O pedido ────────────────────────────────────────────────────────

    public static TipoDePedido Classificar(string prompt)
    {
        // A fingerprint de um host novo: "(yes/no/[fingerprint])?", e a repetição
        // "Please type 'yes', 'no' or the fingerprint: ".
        if (prompt.Contains("(yes/no", StringComparison.OrdinalIgnoreCase)
            || prompt.Contains("type 'yes'", StringComparison.OrdinalIgnoreCase))
            return TipoDePedido.SimNao;

        // O git pede o usuário com eco: "Username for 'https://…': ".
        if (prompt.TrimStart().StartsWith("Username", StringComparison.OrdinalIgnoreCase))
            return TipoDePedido.Texto;

        return TipoDePedido.Senha;
    }

    /// <summary>
    /// Se o executável que pediu mora numa pasta que só o administrador escreve. Um ssh.exe
    /// copiado para outra pasta pode ser qualquer coisa com esse nome.
    /// </summary>
    public static bool Confiavel(string? caminho)
    {
        if (string.IsNullOrWhiteSpace(caminho)) return false;

        string sistema = Environment.GetFolderPath(Environment.SpecialFolder.System);
        string programas = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string[] pastas =
        [
            Path.Combine(sistema, "OpenSSH"),
            Path.Combine(programas, "OpenSSH"),
            Path.Combine(programas, "Git")
        ];

        string cheio;
        try { cheio = Path.GetFullPath(caminho); }
        catch { return false; }

        foreach (var pasta in pastas)
            if (cheio.StartsWith(pasta + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return true;

        return false;
    }

    /// <summary>
    /// O coração do servidor, sem o pipe. Devolve o que o askpass deve escrever, ou null
    /// para ele sair com erro (token inválido ou usuário cancelou).
    /// </summary>
    public async Task<string?> ResponderAsync(string token, string prompt, int pidDoAskpass)
    {
        Sessao? sessao;
        lock (_trava) _sessoes.TryGetValue(token, out sessao);
        if (sessao is null) return null;

        string? solicitante = null;
        try { solicitante = _quemPede(pidDoAskpass); } catch { }
        bool confiavel = Confiavel(solicitante);
        var tipo = Classificar(prompt);

        if (tipo == TipoDePedido.Senha && confiavel)
        {
            lock (_trava)
            {
                if (_lembradas.TryGetValue(prompt, out var lembrada))
                {
                    if (sessao.RespondidosDaMemoria.Add(prompt))
                        return lembrada;

                    // O mesmo prompt de novo, no mesmo comando: a lembrada foi recusada.
                    _lembradas.Remove(prompt);
                }
            }
        }

        var pedido = new PedidoDeSenha(prompt, sessao.Comando, solicitante, confiavel, tipo);

        await _umaJanela.WaitAsync();
        sessao.ComecouAEsperar();
        RespostaDoUsuario? resposta;
        try
        {
            resposta = await _perguntar(pedido);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ASKPASS] a janela falhou — {ex.Message}");
            resposta = null;
        }
        finally
        {
            sessao.ParouDeEsperar();
            _umaJanela.Release();
        }

        if (resposta?.Texto is null) return null;

        // Só senha, só de quem é confiável. "yes" de fingerprint é decisão de cada vez.
        if (resposta.Lembrar && tipo == TipoDePedido.Senha && confiavel)
            lock (_trava) _lembradas[prompt] = resposta.Texto;

        return resposta.Texto;
    }

    /// <summary>Esquece as senhas lembradas.</summary>
    public void Esquecer()
    {
        lock (_trava) _lembradas.Clear();
    }

    // ── O pipe ──────────────────────────────────────────────────────────

    private record Mensagem(string? Token, string? Prompt);
    private record Retorno(bool Ok, string? Texto);

    private async Task EscutarAsync(CancellationToken fim)
    {
        while (!fim.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = new NamedPipeServerStream(
                    NomeDoPipe, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

                await pipe.WaitForConnectionAsync(fim);

                // Cada conexão no seu fio: um pedido esperando o usuário não pode travar a
                // porta para o próximo (que vai esperar a vez da janela, mas conectado).
                var conectado = pipe;
                pipe = null;
                _ = Task.Run(() => AtenderAsync(conectado));
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Console.WriteLine($"[ASKPASS] escuta falhou — {ex.Message}");
                try { await Task.Delay(500, fim); } catch { break; }
            }
            finally
            {
                pipe?.Dispose();
            }
        }
    }

    private async Task AtenderAsync(NamedPipeServerStream pipe)
    {
        await using var _ = pipe;
        try
        {
            using var leitor = new StreamReader(pipe, new UTF8Encoding(false), false, 1024, leaveOpen: true);
            string? linha = await leitor.ReadLineAsync();
            if (linha is null) return;

            var msg = JsonSerializer.Deserialize<Mensagem>(linha);
            string? texto = null;

            if (msg?.Token is { Length: > 0 } && msg.Prompt is not null)
            {
                int pid = 0;
                if (GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint clientePid))
                    pid = (int)clientePid;

                texto = await ResponderAsync(msg.Token, msg.Prompt, pid);
            }

            byte[] saida = new UTF8Encoding(false).GetBytes(
                JsonSerializer.Serialize(new Retorno(texto is not null, texto)) + "\n");
            await pipe.WriteAsync(saida);
            await pipe.FlushAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ASKPASS] pedido falhou — {ex.Message}");
        }
    }

    public void Dispose()
    {
        _fim.Cancel();
        Esquecer();
    }

    // ── Quem pediu ──────────────────────────────────────────────────────

    /// <summary>
    /// O executável do PAI do processo dado. O askpass é chamado pelo ssh (ou git); o pai
    /// dele é quem de fato pede a senha.
    /// </summary>
    public static string? CaminhoDoPai(int pid)
    {
        if (pid <= 0) return null;
        int pai = PidDoPai(pid);
        return pai > 0 ? CaminhoDoExecutavel(pai) : null;
    }

    private static int PidDoPai(int pid)
    {
        IntPtr h = OpenProcess(ProcessQueryLimitedInformation, false, (uint)pid);
        if (h == IntPtr.Zero) return 0;
        try
        {
            var info = new ProcessBasicInformation();
            int status = NtQueryInformationProcess(h, 0, ref info, Marshal.SizeOf(info), out _);
            return status == 0 ? (int)info.InheritedFromUniqueProcessId : 0;
        }
        finally { CloseHandle(h); }
    }

    private static string? CaminhoDoExecutavel(int pid)
    {
        IntPtr h = OpenProcess(ProcessQueryLimitedInformation, false, (uint)pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var buffer = new StringBuilder(1024);
            int tamanho = buffer.Capacity;
            return QueryFullProcessImageName(h, 0, buffer, ref tamanho) ? buffer.ToString(0, tamanho) : null;
        }
        finally { CloseHandle(h); }
    }

    private const uint ProcessQueryLimitedInformation = 0x1000;

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessBasicInformation
    {
        public IntPtr ExitStatus;
        public IntPtr PebBaseAddress;
        public IntPtr AffinityMask;
        public IntPtr BasePriority;
        public IntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeClientProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint clientProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(IntPtr process, int infoClass,
        ref ProcessBasicInformation info, int size, out int returned);
}
