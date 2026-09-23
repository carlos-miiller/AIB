using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OpenAI.Chat;

namespace AIB.Services.Tools;

public class RunCommandTool : ITool
{
    public string Name => Ferramentas.Shell;

    /// <summary>
    /// "Investigar o sistema", que era o que a descrição antiga oferecia, cobre listar uma
    /// pasta, ver se um arquivo existe, procurar texto e criar pasta — e foi o que aconteceu:
    /// 35 de 167 comandos de shell em 49 sessões só tocavam arquivo, com um cartão de
    /// confirmação cada. Por isso a descrição agora diz o que NÃO fazer aqui, e nomeia o que
    /// faz no lugar. "Não é Bash" está escrito porque um <c>ls -la ~/GLPI/ | head -20</c> chegou
    /// a ser proposto ao usuário.
    /// </summary>
    public string Description =>
        "Executa um comando no PowerShell do usuário (não é Bash). Use para docker, git, rede, "
        + "processos, instalação e scripts. NÃO use para arquivo: ler, listar, procurar e gravar "
        + "têm ferramenta própria, e 'write' já cria a pasta que falta. Nada de comando que "
        + $"pergunte algo. Teto de {Prazo.TotalSeconds:0} s.";

    public int RequiredLevel => 2;

    public bool RequiresConfirmation => true;

    /// <summary>A floor list existe para esta ferramenta. Ver <see cref="ITool.PassaPelaFloorList"/>.</summary>
    public bool PassaPelaFloorList => true;

    public CommandConfirmationContext? BuildConfirmationContext(string argumentsJson, int userLevel)
    {
        try
        {
            var args = JsonSerializer.Deserialize<JsonElement>(argumentsJson);
            if (!args.TryGetProperty("command", out var cmd)) return null;

            string comando = cmd.GetString() ?? "";
            if (string.IsNullOrWhiteSpace(comando)) return null;

            var (bateu, razao) = CommandFloorList.Match(comando, userLevel);

            return new CommandConfirmationContext
            {
                Tool = Name,
                Command = comando,
                Level = userLevel,
                Cwd = Environment.CurrentDirectory,
                DenylistHit = bateu,
                DenylistReason = razao ?? "",

                // O shell nunca é dispensado pela lista de pastas, e nunca é limitado por ela.
                // O que ele pode é contar o que viu. Ver EscritaNoComando.
                Aviso = EscritaNoComando.Aviso(comando) ?? ""
            };
        }
        catch (JsonException)
        {
            // JSON ilegível não vira autorização: sem descrever a operação, não há o que aprovar.
            return null;
        }
    }

    /// <summary>
    /// Teto de tempo do comando. Constante porque a descrição, a mensagem de interrupção e o
    /// relógio têm de dizer o mesmo número.
    /// </summary>
    public static readonly TimeSpan Prazo = TimeSpan.FromSeconds(30);

    /// <summary>
    /// A pasta onde o comando roda, dita no schema. A instrução antiga era "use 'pwd' ou
    /// Get-Location se precisar saber o diretório atual" — um turno inteiro gasto para
    /// responder algo que o programa já sabe e que o cartão já mostra.
    /// </summary>
    public ChatTool ChatToolDefinition => ChatTool.CreateFunctionTool(
        functionName: Name,
        functionDescription: Description,
        functionParameters: BinaryData.FromString($$"""
        {
            "type": "object",
            "properties": {
                "command": {
                    "type": "string",
                    "description": "O comando PowerShell exato. Roda em {{EmJson(Environment.CurrentDirectory)}} e não pode pedir nada ao usuário."
                }
            },
            "required": ["command"]
        }
        """)
    );

    /// <summary>
    /// O caminho com as barras invertidas escapadas. Sem isto, <c>C:\Users\Carlo</c> dentro do
    /// schema seria JSON inválido (<c>\U</c> não é escape), e a ferramenta inteira não chegaria
    /// ao modelo.
    /// </summary>
    private static string EmJson(string texto) =>
        texto.Replace("\\", "\\\\", StringComparison.Ordinal)
             .Replace("\"", "\\\"", StringComparison.Ordinal);

    /// <summary>
    /// Recusa antes do portão o que não tem comando nenhum. Ver <see cref="ITool.Validar"/>.
    /// <para>
    /// Sem isto, <c>command</c> ausente ou vazio chegava ao
    /// <see cref="BuildConfirmationContext"/>, que devolvia <c>null</c>, e o modelo recebia
    /// "ACESSO NEGADO: … não foi possível descrever a operação para autorizar" — uma mensagem de
    /// permissão para um erro de sintaxe. Quem lê "ACESSO NEGADO" troca de caminho, de
    /// ferramenta e de nível; nunca de sintaxe.
    /// </para>
    /// </summary>
    public string? Validar(string argumentsJson)
    {
        const string Ilegivel =
            "ERRO: argumentos ilegíveis. Envie um objeto JSON com 'command'.";

        JsonElement args;
        try { args = JsonSerializer.Deserialize<JsonElement>(argumentsJson); }
        catch (JsonException) { return Ilegivel; }

        if (args.ValueKind != JsonValueKind.Object) return Ilegivel;

        if (!args.TryGetProperty("command", out var cmd)
            || cmd.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(cmd.GetString()))
            return "ERRO: o parâmetro 'command' é obrigatório e não pode estar vazio.";

        return null;
    }

    /// <summary>
    /// Converte o bloco CLIXML do stderr em texto legivel, preservando o que houver de erro.
    /// <para>
    /// Com stdout e stderr redirecionados, o PowerShell serializa em CLIXML tudo que nao e
    /// texto puro — progresso, aviso, e tambem os REGISTROS DE ERRO. A primeira versao disto
    /// jogava o bloco inteiro fora, e com isso cegava o modelo: um cmdlet que falhasse
    /// devolvia "Comando executado com sucesso (sem saida)", e ele seguia adiante achando que
    /// nao havia o que corrigir.
    /// </para>
    /// <para>
    /// Agora o bloco e desmontado: os elementos de texto viram linhas, e as de progresso — as
    /// unicas que nao dizem nada ao modelo — saem. O que estiver fora do bloco e preservado
    /// como veio.
    /// </para>
    /// </summary>
    internal static string SemClixml(string? stderr)
    {
        if (string.IsNullOrEmpty(stderr)) return "";

        int inicio = stderr.IndexOf("#< CLIXML", StringComparison.Ordinal);
        if (inicio < 0) return stderr;

        int fim = stderr.LastIndexOf("</Objs>", StringComparison.Ordinal);

        string antes = stderr.Substring(0, inicio);
        string depois = fim < 0 ? "" : stderr.Substring(fim + "</Objs>".Length);
        string bloco = fim < 0 ? stderr.Substring(inicio) : stderr.Substring(inicio, fim - inicio);

        var partes = new System.Collections.Generic.List<string>();
        if (antes.Trim().Length > 0) partes.Add(antes.Trim());

        string texto = TextoDoClixml(bloco);
        if (texto.Length > 0) partes.Add(texto);

        if (depois.Trim().Length > 0) partes.Add(depois.Trim());

        return string.Join("\n", partes).Trim();
    }

    /// <summary>
    /// Junta os elementos de texto do CLIXML, descartando os objetos de progresso.
    /// <para>
    /// Nao e um desserializador de CLIXML — e uma extracao deliberadamente burra. O que
    /// interessa e a mensagem que o PowerShell escreveria no console; a arvore de tipos nao
    /// tem uso nenhum para quem le do outro lado.
    /// </para>
    /// </summary>
    private static string TextoDoClixml(string bloco)
    {
        // Objetos de progresso inteiros saem antes: o texto deles ("Preparando modulos para
        // primeiro uso") seria colhido junto e nao diz nada sobre a falha.
        string limpo = System.Text.RegularExpressions.Regex.Replace(
            bloco,
            "<Obj[^>]*S=\"progress\".*?</Obj>",
            "",
            System.Text.RegularExpressions.RegexOptions.Singleline
            | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        var linhas = new System.Collections.Generic.List<string>();

        foreach (System.Text.RegularExpressions.Match m in
                 System.Text.RegularExpressions.Regex.Matches(limpo, "<S[^>]*>(.*?)</S>",
                     System.Text.RegularExpressions.RegexOptions.Singleline))
        {
            string valor = System.Net.WebUtility.HtmlDecode(m.Groups[1].Value)
                .Replace("_x000D__x000A_", "\n")
                .Replace("_x000A_", "\n")
                .Trim();

            if (valor.Length > 0) linhas.Add(valor);
        }

        return string.Join("\n", linhas).Trim();
    }

    /// <summary>Teto do texto devolvido ao modelo. Vale também para a saída das skills.</summary>
    public const int TetoDaSaida = 8000;

    /// <summary>
    /// O que vai na frente de todo comando.
    /// <para>
    /// A barra de progresso é calada porque, com os fluxos redirecionados, o PowerShell
    /// serializa em CLIXML tudo o que não é texto: um <c>Get-ChildItem -Recurse</c> devolvia
    /// meio kilobyte de <c>&lt;Obj S="progress"&gt;</c> junto de três linhas úteis, e isso ia
    /// inteiro para o histórico e para o resumo.
    /// </para>
    /// <para>
    /// A codificação é fixada para o filho ESCREVER em UTF-8, já que o outro lado
    /// (<c>StandardOutputEncoding</c>) o lê assim. Vai dentro de <c>try</c> de propósito: sem
    /// console anexado, atribuir <c>[Console]::OutputEncoding</c> pode falhar, e uma falha aqui
    /// derrubaria TODO comando — o preço de não conseguir é a acentuação como era antes, que é
    /// o que já havia.
    /// </para>
    /// </summary>
    public const string Prefixo =
        "$ProgressPreference = 'SilentlyContinue'; "
        + "try { [Console]::OutputEncoding = [Text.Encoding]::UTF8 } catch { }; "
        + "$OutputEncoding = [Text.Encoding]::UTF8; ";

    /// <summary>
    /// O que <see cref="Montar"/> devolve quando deu certo e não houve saída. Constante porque a
    /// <see cref="ExecuteSkillTool"/> troca este texto pelo dela — "Comando executado" numa
    /// habilidade confunde quem lê.
    /// </summary>
    public const string SucessoSemSaida = "Comando executado com sucesso (sem saída).";

    /// <summary>
    /// O resultado que o modelo lê, com a FALHA dita na primeira palavra.
    /// <para>
    /// Antes o código de saída nem era lido, e o erro do PowerShell chegava misturado à saída
    /// sem marca nenhuma. Tudo o que decide "falhou" no programa — o chip vermelho, o recado ao
    /// modelo, o [FALHOU] do capítulo, a seção Pendente — olha para o "ERRO" no começo do
    /// resultado. Numa conversa real, 4 de 9 comandos de um capítulo falharam (script
    /// inexistente, pasta apagada, parâmetro com tipo errado) e todos entraram na memória como
    /// sucesso; o resumidor inventou uma causa para o que ninguém tinha marcado como erro.
    /// </para>
    /// <para>
    /// Dois sinais, e nenhum sozinho basta. O código de saída pega o comando nativo que falhou e o
    /// erro que encerra o script; o erro que NÃO encerra (um Get-Content numa pasta que sumiu, e o
    /// script segue) sai com código 0, e só aparece como registro de erro no CLIXML. Stderr em
    /// texto puro não conta: git, npm e afins escrevem progresso ali com sucesso.
    /// </para>
    /// </summary>
    public static string Montar(string? stdout, string? stderr, int codigoDeSaida)
    {
        string saida = ((stdout ?? "") + "\n" + SemClixml(stderr)).Trim();
        var erros = ErrosDoClixml(stderr);

        // Programa nativo que escreve em stderr com 2>&1 vira registro de erro "NativeCommandError"
        // mesmo quando deu certo. Com código 0, é progresso de git, não falha.
        if (codigoDeSaida == 0 && erros.Any(e => e.Contains("NativeCommandError", StringComparison.Ordinal)))
            erros = Array.Empty<string>();

        // Repetição colapsada e corte que guarda começo, erros e fim — ou, com o filtro desligado,
        // o corte cego antigo em TetoDaSaida. Ver FiltroDeSaida.
        saida = FiltroDeSaida.Aplicar(saida, TetoDaSaida);

        // Só o PowerShell falando, e o programa não disse nada que pareça erro: não é falha.
        //
        // Visto numa conversa inteira: `docker exec glpi php teste.php` imprimia "HOOK chamado:
        // Change status=6" no stderr do container. O PowerShell virava isso num
        // NativeCommandError e devolvia código 1. O teste tinha PASSADO, e o modelo leu "ERRO",
        // foi consertar o que não estava quebrado e a pendência sobreviveu à conversa toda.
        // O corte é pela MARCA: "cannot open", "não é reconhecido" e afins continuam falha;
        // saída comum do programa, não.
        bool soRuidoDoPowerShell =
            codigoDeSaida != 0
            && erros.Count > 0
            && erros.All(e => e.Contains("NativeCommandError", StringComparison.Ordinal) && !TemMarcaDeErro(e));

        if (soRuidoDoPowerShell)
        {
            string aviso = $"(o programa escreveu no stderr e saiu com código {codigoDeSaida}; "
                           + "nada na saída indica erro — se o resultado importa, confira nela)";
            return saida.Length == 0 ? SucessoSemSaida + "\n" + aviso : saida + "\n\n" + aviso;
        }

        bool falhou = codigoDeSaida != 0 || erros.Count > 0;

        if (!falhou)
            return saida.Length == 0 ? SucessoSemSaida : saida;

        string primeira = erros.FirstOrDefault()
                          ?? saida.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0)
                          ?? "";
        if (primeira.Length > 200) primeira = primeira[..200] + "…";

        string cabeca = codigoDeSaida != 0
            ? $"ERRO (código de saída {codigoDeSaida})"
            : "ERRO: o comando continuou, mas houve erro";

        if (primeira.Length > 0) cabeca += ": " + primeira;

        return saida.Length == 0 || saida == primeira
            ? cabeca
            : cabeca + "\n\nSaída completa:\n" + saida;
    }

    /// <summary>
    /// Marcas que aparecem em erro DE VERDADE, e não na saída comum de um programa.
    /// <para>
    /// Deliberadamente estreita. "invalid" sozinho ficou de fora: um <c>grep</c> por "Invalid
    /// plugin directory" devolve a linha encontrada, que é sucesso. O custo de não reconhecer um
    /// erro aqui é o modelo ler a saída e perceber; o custo de reconhecer demais é ele desfazer
    /// trabalho que deu certo — foi o que aconteceu.
    /// </para>
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex MarcaDeErro = new(
        @"(?:^|\W)(erro:|error:|exception|traceback|fatal|panic:|segmentation fault"
        + @"|not found|no such file|cannot open|cannot find|unable to|denied"
        + @"|is not recognized|não é reconhecido|nao e reconhecido"
        + @"|não foi possível|nao foi possivel)",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase
        | System.Text.RegularExpressions.RegexOptions.Compiled);

    internal static bool TemMarcaDeErro(string? texto) =>
        !string.IsNullOrEmpty(texto) && MarcaDeErro.IsMatch(texto);

    /// <summary>
    /// As mensagens de erro serializadas no CLIXML, uma por registro. O PowerShell quebra cada
    /// registro em vários <c>&lt;S S="Error"&gt;</c> seguidos (a mensagem e as linhas "+ No
    /// linha:1…"); a primeira parte de cada grupo é a mensagem.
    /// </summary>
    internal static IReadOnlyList<string> ErrosDoClixml(string? stderr)
    {
        if (string.IsNullOrEmpty(stderr) || stderr.IndexOf("S=\"Error\"", StringComparison.Ordinal) < 0)
            return Array.Empty<string>();

        string texto = string.Concat(
            System.Text.RegularExpressions.Regex.Matches(stderr, "<S S=\"Error\">(.*?)</S>",
                    System.Text.RegularExpressions.RegexOptions.Singleline)
                .Select(m => System.Net.WebUtility.HtmlDecode(m.Groups[1].Value)
                    .Replace("_x000D__x000A_", "\n")
                    .Replace("_x000A_", "\n")));

        // Um registro termina na linha "FullyQualifiedErrorId"; a mensagem é a primeira linha
        // não vazia depois do anterior. Sem essa linha, cada linha que não começa com "+" conta.
        var erros = new List<string>();
        bool esperandoMensagem = true;

        foreach (string bruta in texto.Split('\n'))
        {
            string linha = bruta.Trim();
            if (linha.Length == 0) continue;

            if (linha.StartsWith("+", StringComparison.Ordinal))
            {
                if (linha.Contains("FullyQualifiedErrorId", StringComparison.Ordinal))
                {
                    if (linha.Contains("NativeCommandError", StringComparison.Ordinal) && erros.Count > 0)
                        erros[^1] += " [NativeCommandError]";
                    esperandoMensagem = true;
                }
                continue;
            }

            if (esperandoMensagem)
            {
                erros.Add(linha);
                esperandoMensagem = false;
            }
        }

        return erros;
    }

    public async Task<string> ExecuteAsync(string argumentsJson, int userLevel = 1)
    {
        try
        {
            var args = JsonSerializer.Deserialize<JsonElement>(argumentsJson);
            if (!args.TryGetProperty("command", out var cmdElement))
                return "ERRO: o parâmetro 'command' é obrigatório e não pode estar vazio.";

            string command = cmdElement.GetString() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(command))
                return "ERRO: o parâmetro 'command' é obrigatório e não pode estar vazio.";

            Console.WriteLine($"[TOOL: shell] Executando: {command}");

            // -EncodedCommand (Base64 UTF-16LE) elimina o problema de quoting inteiro. O escape
            // anterior era command.Replace("\"", "\\\""), e a barra invertida não é o caractere
            // de escape do PowerShell (é a crase) — qualquer comando terminado em separador de
            // caminho do Windows era corrompido antes de rodar.
            // O prefixo cala a barra de progresso. Com stdout/stderr redirecionados, o
            // PowerShell serializa os fluxos que não são texto em CLIXML e os despeja no
            // stderr: um Get-ChildItem -Recurse devolvia meio kilobyte de
            // <Obj S="progress">…</Obj> junto com três linhas de resultado útil. Isso ia
            // inteiro para o histórico e para o resumo — contexto pago para dizer
            // "Preparando módulos para primeiro uso".
            string encoded = Convert.ToBase64String(
                Encoding.Unicode.GetBytes(Prefixo + command));

            var startInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -EncodedCommand {encoded}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,

                // Os dois lados combinados em UTF-8. Sem isto o filho escreve na página de
                // código do console e o .NET decodifica como outra coisa: 56 linhas do
                // histórico voltaram com "Diret�rio" e "conclu��do". Isso entra no contexto do
                // modelo, na memória e na tela — e um nome de arquivo acentuado que volte assim
                // e seja reenviado numa chamada seguinte é um caminho que não existe.
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,

                // Entrada redirecionada e fechada logo apos o Start: sem isto o powershell
                // herda o console do app, e qualquer coisa que pergunte algo (Read-Host, um
                // cmdlet com parametro obrigatorio ausente, uma confirmacao) fica parada ate
                // o teto de 30s e volta como "Timeout" - uma mensagem que esconde a causa.
                // Com a entrada fechada o prompt le EOF e o erro real volta na hora.
                RedirectStandardInput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Environment.CurrentDirectory
            };

            using var process = new Process { StartInfo = startInfo };
            process.Start();

            try { process.StandardInput.Close(); } catch { }

            // Os dois pipes são lidos EM PARALELO. Ler stdout até o fim e só depois stderr
            // trava assim que o filho enche o buffer do stderr (~4KB): ele bloqueia escrevendo,
            // nós bloqueamos lendo o outro pipe, e o usuário via a mentira "demorou mais de
            // 30 segundos" num comando que nunca teve chance de terminar.
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();

            using var timeoutCts = new CancellationTokenSource(Prazo);
            bool timedOut = false;
            try
            {
                await process.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException)
            {
                timedOut = true;
            }

            if (timedOut)
            {
                // entireProcessTree: matar só o powershell.exe deixava netos rodando sem
                // supervisão e a task leitora pendurada nos handles do processo.
                try { process.Kill(entireProcessTree: true); } catch { }
                try { await process.WaitForExitAsync(); } catch { }
                await Task.WhenAny(Task.WhenAll(stdoutTask, stderrTask), Task.Delay(2000));
                // Dizer só "Timeout" deixa o modelo sem próximo passo, e ele repete o comando.
                // As duas coisas que ele precisa saber: que o mundo pode ter mudado mesmo
                // assim, e qual é o escopo a reduzir.
                return $"ERRO: o comando passou de {Prazo.TotalSeconds:0} s e foi interrompido. "
                       + "Nada garante que ele não mudou nada antes disso. Reduza o escopo (uma "
                       + $"pasta em vez do disco) ou use '{Ferramentas.Procurar}'/"
                       + $"'{Ferramentas.Buscar}', que já são a busca.";
            }

            return Montar(await stdoutTask, await stderrTask, process.ExitCode);
        }
        catch (Exception ex)
        {
            return $"ERRO fatal ao executar comando: {ex.Message}";
        }
    }
}
