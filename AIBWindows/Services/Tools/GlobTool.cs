using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using OpenAI.Chat;

namespace AIB.Services.Tools;

/// <summary>
/// Acha arquivos por padrão de nome.
/// <para>
/// Existe porque a ausência dela custava turnos inteiros. Sem uma ferramenta de descoberta, o
/// modelo reinventa a busca pelo shell — visto em produção, dois turnos seguidos gastos em
/// <c>Get-ChildItem TEMP</c> e <c>Get-ChildItem TEMP -Recurse</c>, uns seis minutos, para
/// descobrir o que uma chamada responderia. E o <c>read</c> numa pasta ainda respondia "arquivo
/// não encontrado", escondendo a informação que já tinha na mão.
/// </para>
/// <para>
/// SOMENTE LEITURA: lista nomes, nunca abre conteúdo. Por isso não passa pelo portão de
/// confirmação — saber que um arquivo existe não muda a máquina de ninguém.
/// </para>
/// </summary>
public sealed class GlobTool : ITool
{
    /// <summary>
    /// Teto de resultados. Uma pasta de projeto tem dezenas de milhares de arquivos, e despejar
    /// todos consumiria a janela de contexto inteira para responder "onde está o html".
    /// </summary>
    public const int Teto = 100;

    public string Name => Ferramentas.Procurar;

    /// <summary>
    /// Teto de tempo da varredura. O <c>shell</c> tem 30 s e esta ferramenta não tinha nada —
    /// uma busca recursiva a partir da pasta do usuário podia durar o turno inteiro. É o mesmo
    /// caso que estourou os 30 s do shell numa sessão real.
    /// </summary>
    public static readonly TimeSpan Prazo = TimeSpan.FromSeconds(10);

    public string Description =>
        $"Acha arquivos por padrão de nome ('*.html', '**/*.cs'; '**' entra nas subpastas). "
        + $"Devolve os caminhos completos, do mais recente, até {Teto}. Um padrão por chamada. "
        + "Use quando não souber o caminho exato, no lugar de dir/Get-ChildItem.";

    public int RequiredLevel => 1;

    public ChatTool ChatToolDefinition => ChatTool.CreateFunctionTool(
        functionName: Name,
        functionDescription: Description,
        functionParameters: BinaryData.FromString("""
        {
            "type": "object",
            "properties": {
                "pattern": {
                    "type": "string",
                    "description": "Um padrão de nome: '*.xlsx', '**/*.cs', 'users.*'. ** entra nas subpastas. Um por chamada."
                },
                "path": {
                    "type": "string",
                    "description": "Pasta onde procurar. Padrão: a pasta do usuário."
                }
            },
            "required": ["pattern"]
        }
        """)
    );

    public Task<string> ExecuteAsync(string argumentsJson, int userLevel = 1)
    {
        string padrao, raiz;

        try
        {
            var args = JsonSerializer.Deserialize<JsonElement>(argumentsJson);

            padrao = (args.TryGetProperty("pattern", out var p) ? p.GetString() : null) ?? "";
            raiz = PathArgumentRepair.Normalize(
                args.TryGetProperty("path", out var d) ? d.GetString() : null);
        }
        catch (JsonException ex)
        {
            return Task.FromResult($"ERRO: argumentos ilegíveis ({ex.Message}).");
        }

        if (padrao.Trim().Length == 0)
            return Task.FromResult("ERRO: o parâmetro 'pattern' é obrigatório.");

        if (raiz.Length == 0)
            raiz = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        if (!Directory.Exists(raiz))
            return Task.FromResult(PreVooDeCaminho.Conferir($"\"{raiz}\"")
                                   ?? $"ERRO: a pasta '{raiz}' não existe.");

        Console.WriteLine($"[TOOL: {Name}] {padrao} em {raiz}");

        return Task.FromResult(Procurar(raiz, padrao));
    }

    /// <summary>
    /// A busca em si. Separada e pública porque é ela que os ensaios cobrem, e porque o valor
    /// desta ferramenta está no TEXTO da resposta tanto quanto na lista.
    /// </summary>
    public static string Procurar(string raiz, string padrao, TimeSpan? prazo = null)
    {
        try
        {
            bool recursivo = padrao.Contains("**", StringComparison.Ordinal);
            string mascara = padrao.Replace("**/", "").Replace("**\\", "").Replace("**", "*");

            var relogio = System.Diagnostics.Stopwatch.StartNew();
            TimeSpan teto = prazo ?? Prazo;
            bool parouNoPrazo = false;

            // A varredura é consumida aos poucos, e não materializada de uma vez, justamente
            // para poder parar no meio: o LINQ de antes ordenava o disco inteiro antes de
            // pegar os 101 primeiros.
            var achados = new List<FileInfo>();

            foreach (string caminho in Directory.EnumerateFiles(raiz, mascara, Opcoes(recursivo)))
            {
                if (relogio.Elapsed >= teto) { parouNoPrazo = true; break; }

                try { achados.Add(new FileInfo(caminho)); } catch { }
            }

            achados.Sort((a, b) => b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc));

            string rotuloDoPrazo = parouNoPrazo
                ? $"\n(parei em {teto.TotalSeconds:0} s com {achados.Count} arquivo(s) achado(s); "
                  + "a resposta pode estar incompleta — aponte 'path' para uma pasta mais específica)"
                : "";

            if (achados.Count == 0)
            {
                if (parouNoPrazo)
                    return $"Nenhum arquivo casa com '{padrao}' em '{raiz}' no que deu para "
                           + $"varrer em {teto.TotalSeconds:0} s. A resposta está incompleta — "
                           + "aponte 'path' para uma pasta mais específica.";

                // Nada achado não é um beco: dizer o que EXISTE na pasta é o que impede a
                // próxima tentativa de ser outro chute. Foi a lição das quatro chamadas
                // idênticas a ler-planilha.
                var vizinhos = Directory.EnumerateFileSystemEntries(raiz)
                    .Select(Path.GetFileName)
                    .Take(20)
                    .ToList();

                return vizinhos.Count == 0
                    ? $"Nenhum arquivo casa com '{padrao}' em '{raiz}', que está vazia."
                    : $"Nenhum arquivo casa com '{padrao}' em '{raiz}'.\n"
                      + $"O que existe lá: {string.Join(", ", vizinhos)}."
                      + (recursivo ? "" : "\nUse '**/' no começo do padrão para entrar nas subpastas.");
            }

            var linhas = new List<string>
            {
                $"{Math.Min(achados.Count, Teto)} arquivo(s) para '{padrao}', do mais recente:"
            };

            linhas.AddRange(achados.Take(Teto).Select(f => f.FullName));

            if (achados.Count > Teto)
                linhas.Add($"(parei no teto de {Teto}; refine o padrão ou aponte 'path' para uma "
                           + "pasta mais específica)");

            return string.Join("\n", linhas) + rotuloDoPrazo;
        }
        catch (Exception ex)
        {
            return $"ERRO ao procurar: {ex.Message}";
        }
    }

    /// <summary>
    /// Como a varredura anda pelo disco.
    /// <para>
    /// A sobrecarga com <see cref="SearchOption"/> usa <c>EnumerationOptions.Compatible</c>, que
    /// traz <c>IgnoreInaccessible = false</c>. Com a raiz padrão sendo a pasta do usuário, a
    /// primeira junção protegida (<c>AppData\Local\Application Data</c>, <c>Cookies</c>, <c>Meus
    /// Documentos</c>) lança <c>UnauthorizedAccessException</c> e a busca inteira morre por causa
    /// de uma pasta — justo na raiz em que o modelo mais chamaria esta ferramenta.
    /// </para>
    /// <para>
    /// Ponto de arquivo (<c>ReparsePoint</c>) NÃO é pulado, de propósito: no Windows 11 a Área de
    /// Trabalho, Documentos e Imagens costumam ser redirecionadas para o OneDrive por junção, e
    /// pular reparse esconderia as pastas que o usuário mais usa. Contra os laços de junção quem
    /// defende é o <see cref="Prazo"/>.
    /// </para>
    /// </summary>
    public static EnumerationOptions Opcoes(bool recursivo) => new()
    {
        RecurseSubdirectories = recursivo,
        IgnoreInaccessible = true,
        MatchType = MatchType.Win32,

        // Zero, como o padrão antigo: arquivo oculto ou de sistema continua aparecendo. O
        // padrão de EnumerationOptions pularia os dois, e isso seria mudança silenciosa.
        AttributesToSkip = 0
    };
}
