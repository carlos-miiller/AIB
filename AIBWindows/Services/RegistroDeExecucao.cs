using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace AIB.Services;

/// <summary>
/// Espelha tudo o que vai para o console num arquivo, um por execução.
/// <para>
/// Existe porque diagnosticar o vigia dependia de copiar o terminal à mão, e o terminal do
/// PowerShell rola fora e corta. Um digest imprime dezenas de milhares de caracteres; o que
/// interessa costuma estar no pedaço que já saiu da tela.
/// </para>
/// <para>
/// ATENÇÃO AO QUE ISSO GRAVA. O console já imprime o prompt inteiro que vai ao modelo, e o
/// prompt da triagem carrega assunto, remetente e corpo dos e-mails. Ligar isto passa a
/// GRAVAR esse conteúdo em disco — que é justamente o que a regra 3 do vigia evita no resto do
/// programa. É opt-in, é diagnóstico, e o cabeçalho do arquivo avisa. O que NUNCA entra aqui é
/// senha ou chave: elas não passam pelo console em lugar nenhum, e o
/// <see cref="Redigir"/> apara o que escapar.
/// </para>
/// </summary>
public sealed class RegistroDeExecucao : IDisposable
{
    /// <summary>
    /// Quantos arquivos ficam. Um por execução vira lixo acumulado depressa — quem abre e
    /// fecha a AIB dez vezes num dia deixa dez arquivos, e ninguém volta ao de anteontem.
    /// </summary>
    public const int ArquivosMantidos = 20;

    private readonly TextWriter _saidaOriginal;
    private readonly TextWriter _erroOriginal;
    private readonly StreamWriter _arquivo;
    private bool _fechado;

    private RegistroDeExecucao(StreamWriter arquivo, TextWriter saida, TextWriter erro, string caminho)
    {
        _arquivo = arquivo;
        _saidaOriginal = saida;
        _erroOriginal = erro;
        Caminho = caminho;
    }

    public string Caminho { get; }

    /// <summary>
    /// Liga o espelho, se a configuração pedir. Devolve <c>null</c> quando está desligado ou
    /// quando o arquivo não pôde ser criado — diagnóstico que derruba o programa não serve.
    /// </summary>
    public static RegistroDeExecucao? Iniciar(UserAppSettings config, string? pastaDeLogs = null)
    {
        if (config == null || !config.ExecutionLogging) return null;

        try
        {
            string pasta = pastaDeLogs ?? DirectoryService.LogsDir;
            Directory.CreateDirectory(pasta);

            string caminho = Path.Combine(pasta, NomeDoArquivo(DateTime.Now));

            var arquivo = new StreamWriter(caminho, append: false, Encoding.UTF8) { AutoFlush = true };

            var registro = new RegistroDeExecucao(arquivo, Console.Out, Console.Error, caminho);
            registro.EscreverCabecalho(config);

            // Synchronized porque o console é escrito de várias threads ao mesmo tempo — o laço
            // do agente, o vigia e a interface. Sem isso as linhas se entrelaçam no meio.
            Console.SetOut(TextWriter.Synchronized(new Espelho(registro._saidaOriginal, arquivo)));
            Console.SetError(TextWriter.Synchronized(new Espelho(registro._erroOriginal, arquivo)));

            Limpar(pasta);

            Console.WriteLine($"[LOG] Registro desta execução em {caminho}");
            return registro;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[LOG] não consegui abrir o registro de execução: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Nome ordenável por data. Público para os ensaios e para quem for procurar o arquivo:
    /// listar a pasta em ordem alfabética já põe o mais recente por último.
    /// </summary>
    public static string NomeDoArquivo(DateTime quando) =>
        $"execucao-{quando:yyyy-MM-dd-HHmmss}.log";

    /// <summary>
    /// Apaga os mais antigos, deixando <see cref="ArquivosMantidos"/>. Só mexe em arquivos com
    /// o prefixo desta feature: a pasta de logs também guarda a auditoria, que é outra coisa e
    /// não pode ser varrida junto.
    /// </summary>
    public static void Limpar(string pasta, int manter = ArquivosMantidos)
    {
        try
        {
            var antigos = Directory.GetFiles(pasta, "execucao-*.log")
                .OrderByDescending(a => a, StringComparer.Ordinal)
                .Skip(manter)
                .ToList();

            foreach (string velho in antigos)
            {
                try { File.Delete(velho); } catch { }
            }
        }
        catch
        {
            // Não conseguir limpar não é motivo para não registrar.
        }
    }

    /// <summary>
    /// Apara o que parecer credencial. O console não imprime senha nem chave em lugar nenhum —
    /// isto é a segunda linha de defesa, para o dia em que alguém acrescentar um
    /// <c>Console.WriteLine</c> descuidado e o arquivo virar o vazamento.
    /// </summary>
    public static string Redigir(string? linha)
    {
        string texto = linha ?? "";

        foreach (string campo in new[] { "\"api_key\"", "\"apiKey\"", "\"password\"", "\"senha\"", "Authorization:" })
        {
            int i = texto.IndexOf(campo, StringComparison.OrdinalIgnoreCase);
            if (i < 0) continue;

            int fim = texto.IndexOf('\n', i);
            if (fim < 0) fim = texto.Length;

            texto = texto.Substring(0, i) + campo + " [REDIGIDO]" + texto.Substring(fim);
        }

        return texto;
    }

    private void EscreverCabecalho(UserAppSettings config)
    {
        _arquivo.WriteLine("═══════════════════════════════════════════════════════════════════");
        _arquivo.WriteLine($" AIB — registro de execução");
        _arquivo.WriteLine($" início: {DateTime.Now:yyyy-MM-dd HH:mm:ss} ({TimeZoneInfo.Local.StandardName})");
        _arquivo.WriteLine("═══════════════════════════════════════════════════════════════════");
        _arquivo.WriteLine();
        _arquivo.WriteLine("ESTE ARQUIVO CONTÉM TUDO O QUE FOI PARA O CONSOLE, incluindo os prompts");
        _arquivo.WriteLine("enviados ao modelo. O prompt da triagem carrega assunto, remetente e corpo");
        _arquivo.WriteLine("dos seus e-mails. Confira antes de mandar para alguém.");
        _arquivo.WriteLine();
        _arquivo.WriteLine($"máquina .......... {Environment.MachineName} · {Environment.OSVersion}");
        _arquivo.WriteLine($"processadores .... {Environment.ProcessorCount}");
        _arquivo.WriteLine($".NET ............. {Environment.Version}");
        _arquivo.WriteLine($"pasta de dados ... {DirectoryService.DataDir}");
        _arquivo.WriteLine();
        _arquivo.WriteLine($"provedor ......... {config.AiProvider}");
        _arquivo.WriteLine($"modelo ........... {config.ModelName}");
        _arquivo.WriteLine($"modelo do Shadow . {config.ShadowModelName}");
        _arquivo.WriteLine($"url .............. {config.ApiUrl}");
        _arquivo.WriteLine($"keep-alive ....... {config.KeepAlive}");
        _arquivo.WriteLine($"personagem ....... {config.ActiveCharacter}");
        _arquivo.WriteLine();
        _arquivo.WriteLine($"ferramentas ...... {(config.EnableIntelligentTools ? "ligadas" : "desligadas")}");
        _arquivo.WriteLine($"confirmar destr. . {(config.ConfirmDangerousCommands ? "sim" : "não")}");
        _arquivo.WriteLine($"Shadow ........... {(config.ShadowAssistantEnabled ? "ligado" : "desligado")}");
        _arquivo.WriteLine($"triagem de e-mail  {(config.ShadowHandlesMail ? "ligada" : "desligada")}");
        _arquivo.WriteLine($"caixas ........... {config.MailAccounts?.Count ?? 0}");
        _arquivo.WriteLine($"janela de e-mail . {config.MailWindowDays} dia(s)");
        _arquivo.WriteLine($"máx. etapas ...... {config.MaxTurnIterations}");
        _arquivo.WriteLine($"compactar em ..... {config.CompactionTrigger:P0}");
        _arquivo.WriteLine($"fatia de memória . {config.MemoryFraction:P0}");
        _arquivo.WriteLine();
        _arquivo.WriteLine("A chave da API e as senhas de app NÃO passam pelo console e não estão aqui.");
        _arquivo.WriteLine("───────────────────────────────────────────────────────────────────");
        _arquivo.WriteLine();
    }

    public void Dispose()
    {
        if (_fechado) return;
        _fechado = true;

        try
        {
            Console.SetOut(_saidaOriginal);
            Console.SetError(_erroOriginal);

            _arquivo.WriteLine();
            _arquivo.WriteLine("───────────────────────────────────────────────────────────────────");
            _arquivo.WriteLine($" fim: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            _arquivo.Flush();
            _arquivo.Dispose();
        }
        catch
        {
        }
    }

    /// <summary>
    /// Escreve nos dois lugares. Falhar no arquivo NUNCA pode calar o console: o terminal é o
    /// que o usuário está olhando, e o registro é a cópia.
    /// </summary>
    private sealed class Espelho : TextWriter
    {
        private readonly TextWriter _console;
        private readonly TextWriter _arquivo;

        public Espelho(TextWriter console, TextWriter arquivo)
        {
            _console = console;
            _arquivo = arquivo;
        }

        public override Encoding Encoding => _console.Encoding;

        public override void Write(char valor)
        {
            _console.Write(valor);
            try { _arquivo.Write(valor); } catch { }
        }

        public override void Write(string? valor)
        {
            _console.Write(valor);
            try { _arquivo.Write(Redigir(valor)); } catch { }
        }

        public override void WriteLine(string? valor)
        {
            _console.WriteLine(valor);
            try { _arquivo.WriteLine(Redigir(valor)); } catch { }
        }

        public override void Flush()
        {
            _console.Flush();
            try { _arquivo.Flush(); } catch { }
        }
    }
}
