using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using OpenAI.Chat;

namespace AIB.Services.Tools;

public class WriteFileTool : ITool
{
    /// <summary>Quanto do conteúdo aparece na prévia do card.</summary>
    private const int TetoDaPrevia = 400;

    public string Name => Ferramentas.Gravar;
    public string Description => "Cria ou sobrescreve um arquivo com o texto fornecido. Sempre use caminhos absolutos.";
    public int RequiredLevel => 2;

    public bool RequiresConfirmation => true;

    /// <summary>
    /// Gravar dentro de uma pasta dispensada não para no card. Fora dela, o card aparece como
    /// sempre. Ver <see cref="PastasSemConfirmacao"/>.
    /// </summary>
    public bool DispensaConfirmacao(string argumentsJson)
        => PastasSemConfirmacao.Dispensa(Ler(argumentsJson)?.Caminho);

    /// <summary>
    /// Recusa antes do portão humano o que não pode dar certo. Ver <see cref="ITool.Validar"/>.
    /// <para>
    /// Sem isto, uma chamada sem <c>content</c> mostrava "CRIAR …" no card, o usuário
    /// autorizava, e só DEPOIS vinha o "parâmetros obrigatórios" — uma autorização pedida para
    /// algo que não ia acontecer.
    /// </para>
    /// </summary>
    public string? Validar(string argumentsJson)
    {
        if (!ObjetoJson(argumentsJson))
            return "ERRO: argumentos ilegíveis. Envie um objeto JSON com 'path' e 'content'.";

        var a = Ler(argumentsJson);
        if (a == null)
            return "ERRO: O parâmetro 'path' é obrigatório e não pode estar vazio.";

        if (a.Conteudo == null)
            return "ERRO: O parâmetro 'content' é obrigatório (texto; vazio cria um arquivo vazio).";

        try { Path.GetFullPath(a.Caminho); }
        catch (Exception ex) { return $"ERRO: caminho inválido '{a.Caminho}': {ex.Message}"; }

        return null;
    }

    /// <summary>
    /// O cartão mostra o caminho ABSOLUTO já resolvido, e não o que o modelo escreveu: é a
    /// diferença entre autorizar "config.json" e autorizar a gravação real em
    /// %APPDATA%\Roaming\Microsoft\Windows\Start Menu\Programs\Startup\config.json.
    /// Fora das pastas sem confirmação, o caminho resolvido é o que o usuário tem para decidir.
    /// </summary>
    public CommandConfirmationContext? BuildConfirmationContext(string argumentsJson, int userLevel)
    {
        var a = Ler(argumentsJson);
        if (a == null || a.Conteudo == null) return null;

        string resolvido;
        try { resolvido = Path.GetFullPath(a.Caminho); }
        catch { return null; }

        bool existe = File.Exists(resolvido);
        string previa = a.Conteudo.Length > TetoDaPrevia
            ? a.Conteudo[..TetoDaPrevia] + "\n...[prévia truncada]"
            : a.Conteudo;

        return new CommandConfirmationContext
        {
            Tool = Name,
            Command = (existe ? "SOBRESCREVER " : "CRIAR ") + resolvido,
            Level = userLevel,
            Cwd = Environment.CurrentDirectory,
            ScriptBody = previa
        };
    }

    public ChatTool ChatToolDefinition => ChatTool.CreateFunctionTool(
        functionName: Name,
        functionDescription: Description,
        functionParameters: BinaryData.FromString("""
        {
            "type": "object",
            "properties": {
                "path": {
                    "type": "string",
                    "description": "O caminho completo e absoluto do arquivo a ser criado."
                },
                "content": {
                    "type": "string",
                    "description": "O conteúdo que será salvo no arquivo."
                }
            },
            "required": ["path", "content"]
        }
        """)
    );

    public async Task<string> ExecuteAsync(string argumentsJson, int userLevel = 1)
    {
        try
        {
            var a = Ler(argumentsJson);
            if (a == null || a.Conteudo == null)
                return "ERRO: Os parâmetros 'path' e 'content' são obrigatórios.";

            string path = a.Caminho;

            Console.WriteLine($"[TOOL: write] Escrevendo em: {path}");

            // Garante que o diretório exista
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            await File.WriteAllTextAsync(path, a.Conteudo);

            // Avisa o modelo do reparo para que ele corrija o escape na próxima chamada, em
            // vez de repetir o erro a cada arquivo.
            string aviso = a.CaminhoReparado
                ? " AVISO: o caminho recebido continha escapes JSON inválidos e foi corrigido. Em JSON, escreva a barra invertida duplicada (C:\\temp\\x.txt)."
                : "";

            return $"SUCESSO: Arquivo salvo corretamente em '{path}'.{aviso}";
        }
        catch (Exception ex)
        {
            return $"ERRO ao escrever arquivo: {ex.Message}";
        }
    }

    // ─────────────────────────────────────────────────────────────────────────

    /// <param name="Conteudo">Null quando <c>content</c> não veio ou não é texto.</param>
    private sealed record Argumentos(string Caminho, string? Conteudo, bool CaminhoReparado);

    /// <summary>
    /// A leitura ÚNICA dos argumentos, usada pela dispensa, pelo pré-voo, pelo card e pela
    /// execução. Eram quatro leituras parecidas, e bastava uma divergir para o card descrever uma
    /// gravação e a execução fazer outra.
    /// <para>
    /// Só o caminho passa pelo reparo. O conteúdo NUNCA: ali uma tabulação ou quebra de linha de
    /// verdade é legítima, e reescrevê-la corromperia o arquivo.
    /// </para>
    /// </summary>
    private static Argumentos? Ler(string argumentsJson)
    {
        try
        {
            var args = JsonSerializer.Deserialize<JsonElement>(argumentsJson);
            if (args.ValueKind != JsonValueKind.Object) return null;

            if (!args.TryGetProperty("path", out var p) || p.ValueKind != JsonValueKind.String) return null;

            string caminho = PathArgumentRepair.Normalize(p.GetString(), out bool reparado);
            if (string.IsNullOrWhiteSpace(caminho)) return null;

            string? conteudo = args.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String
                ? c.GetString()
                : null;

            return new Argumentos(caminho, conteudo, reparado);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool ObjetoJson(string argumentsJson)
    {
        try { return JsonSerializer.Deserialize<JsonElement>(argumentsJson).ValueKind == JsonValueKind.Object; }
        catch (JsonException) { return false; }
    }
}
