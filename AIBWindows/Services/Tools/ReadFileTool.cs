using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using OpenAI.Chat;

namespace AIB.Services.Tools;

/// <summary>
/// Lê um arquivo — por faixa, com número de linha — ou lista uma pasta.
/// <para>
/// Três coisas mudaram, e as três saíram de uma sessão real.
/// </para>
/// <para>
/// FAIXA. Antes vinha o arquivo inteiro até 12.000 caracteres. Numa máquina onde o prefill é o
/// custo dominante, trazer um HTML de duzentas linhas para ver três campos é pagar duzentas
/// linhas. Com <c>offset</c> e <c>limit</c> o modelo pede o pedaço.
/// </para>
/// <para>
/// NÚMERO DE LINHA. É o que faz a edição por trecho ancorar e o que transforma "erro na linha 26,
/// caractere 98" em algo acionável. Sem numeração, o modelo conta linhas de cabeça — e erra.
/// </para>
/// <para>
/// PASTA. Antes, apontar para uma pasta respondia "Arquivo não encontrado" — mentira, ela existe.
/// Foi o primeiro passo em falso de uma cadeia que custou dois turnos: o modelo perguntou pela
/// pasta, ouviu que não existia, e foi listar pelo shell o que esta ferramenta já tinha na mão.
/// </para>
/// </summary>
public class ReadFileTool : ITool
{
    /// <summary>Linhas por leitura no Ollama, quando ninguém pede faixa. Ver <see cref="LimitesDoProvedor"/>.</summary>
    public const int LinhasPadrao = 400;

    /// <summary>Teto de caracteres por linha. Minificado é uma linha de cem mil.</summary>
    public const int TetoDaLinha = 2000;

    /// <summary>Entradas listadas de uma pasta, no Ollama.</summary>
    public const int TetoDaPasta = 100;

    public string Name => Ferramentas.Ler;

    public string Description =>
        "Lê um arquivo de texto com número de linha, ou LISTA uma pasta (basta apontar o caminho "
        + "dela). Use 'offset'/'limit' para um pedaço de arquivo grande. Caminho absoluto.";

    public int RequiredLevel => 1;

    public ChatTool ChatToolDefinition => ChatTool.CreateFunctionTool(
        functionName: Name,
        functionDescription: Description,
        functionParameters: BinaryData.FromString($$"""
        {
            "type": "object",
            "properties": {
                "path": {
                    "type": "string",
                    "description": "Caminho absoluto do arquivo, ou de uma pasta para listá-la."
                },
                "offset": {
                    "type": "integer",
                    "description": "Primeira linha a ler, começando em 1. Padrão: o começo."
                },
                "limit": {
                    "type": "integer",
                    "description": "Quantas linhas ler. Padrão {{LimitesDoProvedor.Atual.LinhasDeLeitura}}."
                }
            },
            "required": ["path"]
        }
        """)
    );

    /// <summary>A pasta de dados do AIB. Nula em produção; os ensaios passam uma temporária.</summary>
    private readonly string? _dados;

    public ReadFileTool(string? raizDeDados = null) => _dados = raizDeDados;

    private Acesso AcessoDe(string argumentsJson) => DadosProtegidos.Leitura(DadosProtegidos.Caminho(argumentsJson), _dados);

    /// <summary>Cofre, configurações e perfil do navegador não são lidos. Ver <see cref="DadosProtegidos"/>.</summary>
    public string? Validar(string argumentsJson) =>
        AcessoDe(argumentsJson) == Acesso.Negado ? DadosProtegidos.Recusa(DadosProtegidos.Caminho(argumentsJson)) : null;

    /// <summary>Ler não pergunta — a não ser dentro da pasta de dados do AIB.</summary>
    public bool PedeConfirmacao(string argumentsJson) => AcessoDe(argumentsJson) == Acesso.Pergunta;

    public CommandConfirmationContext? BuildConfirmationContext(string argumentsJson, int userLevel) =>
        DadosProtegidos.Cartao(Name, "LER", DadosProtegidos.Caminho(argumentsJson), userLevel);

    public Task<string> ExecuteAsync(string argumentsJson, int userLevel = 1) =>
        ExecutarAutorizadoAsync(argumentsJson, userLevel, null);

    /// <summary>Quem chega sem o cartão a uma leitura que pedia cartão é recusado aqui.</summary>
    public Task<string> ExecutarAutorizadoAsync(string argumentsJson, int userLevel, CommandConfirmationContext? autorizado)
    {
        string caminho = DadosProtegidos.Caminho(argumentsJson);

        return AcessoDe(argumentsJson) switch
        {
            Acesso.Negado => Task.FromResult(DadosProtegidos.Recusa(caminho)),
            Acesso.Pergunta when autorizado == null => Task.FromResult(DadosProtegidos.SemCartao(caminho)),
            _ => LerAsync(argumentsJson)
        };
    }

    private async Task<string> LerAsync(string argumentsJson)
    {
        string path;
        int offset, limit;

        try
        {
            var args = JsonSerializer.Deserialize<JsonElement>(argumentsJson);

            if (!args.TryGetProperty("path", out var pathElement))
                return "ERRO: O parâmetro 'path' é obrigatório.";

            // Caractere de controle é ilegal em caminho do Windows, então sua presença só pode
            // vir de um escape JSON mal emitido pelo modelo.
            path = PathArgumentRepair.Normalize(pathElement.GetString());

            offset = Math.Max(1, Numero(args, "offset", 1));
            limit = Math.Max(1, Numero(args, "limit", LimitesDoProvedor.Atual.LinhasDeLeitura));
        }
        catch (JsonException ex)
        {
            return $"ERRO: argumentos ilegíveis ({ex.Message}).";
        }

        if (string.IsNullOrWhiteSpace(path))
            return "ERRO: O caminho do arquivo não pode estar vazio.";

        // A PASTA vem antes do arquivo: apontar para uma pasta é um pedido legítimo, e
        // respondê-lo com "não encontrado" foi o que mandou o modelo procurar pelo shell.
        if (Directory.Exists(path))
        {
            Console.WriteLine($"[TOOL: {Name}] Listando pasta: {path}");
            return Listar(path);
        }

        if (!File.Exists(path))
            return PreVooDeCaminho.Conferir($"\"{path}\"") ?? $"ERRO: '{path}' não existe.";

        Console.WriteLine($"[TOOL: {Name}] Lendo: {path} (linha {offset}, até {limit})");

        try
        {
            var linhas = new List<string>();
            int numero = 0, lidas = 0;
            bool sobrou = false;

            using var leitor = new StreamReader(path, detectEncodingFromByteOrderMarks: true);

            while (await leitor.ReadLineAsync() is { } linha)
            {
                numero++;
                if (numero < offset) continue;

                if (lidas >= limit) { sobrou = true; break; }

                // Arquivo da memória é cifrado por linha; a leitura já passou pelo cartão.
                linhas.Add($"{numero,6}\t{Aparar(ArquivoCifrado.Abrir(linha))}");
                lidas++;
            }

            if (linhas.Count == 0)
                return numero == 0
                    ? $"'{path}' está vazio."
                    : $"'{path}' tem {numero} linha(s); a faixa pedida (a partir da {offset}) "
                      + "está além do fim.";

            var sb = new StringBuilder();
            sb.AppendJoin('\n', linhas);

            if (sobrou)
                sb.Append($"\n\n...[mostrando as linhas {offset} a {offset + lidas - 1}. "
                          + $"Continue com offset={offset + lidas}.]");

            return sb.ToString();
        }
        catch (Exception ex)
        {
            return $"ERRO ao ler arquivo: {ex.Message}";
        }
    }

    /// <summary>
    /// O conteúdo de uma pasta: subpastas primeiro, marcadas com barra, depois os arquivos com
    /// o tamanho. Pública para os ensaios — é a resposta que substitui uma mentira.
    /// </summary>
    public static string Listar(string pasta)
    {
        try
        {
            var subpastas = Directory.GetDirectories(pasta)
                .Select(d => Path.GetFileName(d) + "\\")
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var arquivos = Directory.GetFiles(pasta)
                .Select(a => new FileInfo(a))
                .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                .Select(f => $"{f.Name}  ({Tamanho(f.Length)})")
                .ToList();

            if (subpastas.Count == 0 && arquivos.Count == 0)
                return $"'{pasta}' é uma pasta, e está vazia.";

            var sb = new StringBuilder();
            sb.AppendLine($"'{pasta}' é uma PASTA, com {subpastas.Count} subpasta(s) e "
                          + $"{arquivos.Count} arquivo(s):");

            int tetoDaPasta = LimitesDoProvedor.Atual.ItensDaPasta;
            foreach (string nome in subpastas.Concat(arquivos).Take(tetoDaPasta))
                sb.AppendLine("  " + nome);

            int total = subpastas.Count + arquivos.Count;
            if (total > tetoDaPasta)
                // Dizer quantos ficaram de fora sem dizer como vê-los é um beco: quem lê não
                // tem próximo passo, e o próximo passo vira um Get-ChildItem pelo shell.
                sb.AppendLine($"  (+{total - tetoDaPasta} não listado(s) — teto de {tetoDaPasta}; "
                              + $"use '{Ferramentas.Procurar}' com um padrão para achar o que procura)");

            return sb.ToString().TrimEnd();
        }
        catch (Exception ex)
        {
            return $"ERRO ao listar '{pasta}': {ex.Message}";
        }
    }

    private static int Numero(JsonElement args, string nome, int padrao) =>
        args.TryGetProperty(nome, out var campo)
        && campo.ValueKind == JsonValueKind.Number
        && campo.TryGetInt32(out int valor)
            ? valor
            : padrao;

    /// <summary>Corta linha absurdamente longa. Um arquivo minificado é uma linha só.</summary>
    private static string Aparar(string linha) =>
        linha.Length <= TetoDaLinha ? linha : linha.Substring(0, TetoDaLinha) + "…[linha cortada]";

    private static string Tamanho(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes / (1024.0 * 1024):0.#} MB"
    };
}
