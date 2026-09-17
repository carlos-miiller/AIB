using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using OpenAI.Chat;

namespace AIB.Services.Tools;

/// <summary>
/// Troca um trecho exato de um arquivo, sem reescrever o resto.
/// <para>
/// É a ferramenta que faltava, e a ausência dela era cara de um jeito invisível. Com apenas o
/// <c>write</c>, mudar três campos de um HTML de duzentas linhas obriga o modelo a GERAR as
/// duzentas de novo. Numa máquina que produz 4 tokens por segundo isso são minutos por arquivo,
/// e cada regeneração é uma chance nova de corromper o que estava certo.
/// </para>
/// <para>
/// Visto em produção: a tarefa era replicar um HTML de assinatura trocando nome, e-mail e foto
/// de três pessoas. Sem edição por trecho, o modelo fugiu para o PowerShell e quebrou a sintaxe
/// duas vezes seguidas antes de acertar. Com esta ferramenta ele emitiria trinta caracteres por
/// troca.
/// </para>
/// <para>
/// DUAS INVARIANTES, e as duas existem para que a falha seja preferível ao estrago:
/// <list type="bullet">
/// <item>o trecho tem de existir — não se cria conteúdo por engano;</item>
/// <item>o trecho tem de ser ÚNICO, salvo pedido explícito de trocar todos. Editar a primeira de
/// cinco ocorrências é o erro que ninguém percebe até o arquivo estar errado.</item>
/// </list>
/// </para>
/// </summary>
public sealed class EditFileTool : ITool
{
    /// <summary>Quanto do trecho aparece nas mensagens de erro e no card.</summary>
    private const int TetoDaPrevia = 200;

    public string Name => Ferramentas.Editar;

    public string Description =>
        "Troca um trecho EXATO de um arquivo, preservando todo o resto. Prefira sempre esta "
        + "ferramenta a reescrever o arquivo inteiro. O trecho em 'old_string' precisa existir e "
        + "ser único no arquivo — inclua as linhas em volta se precisar desambiguar, ou passe "
        + "'replace_all' para trocar todas as ocorrências.";

    public int RequiredLevel => 1;

    public bool RequiresConfirmation => true;

    /// <summary>
    /// Editar dentro de uma pasta dispensada não para no card. Fora dela, o card aparece como
    /// sempre. Ver <see cref="PastasSemConfirmacao"/>.
    /// </summary>
    public bool DispensaConfirmacao(string argumentsJson)
        => PastasSemConfirmacao.Dispensa(Ler(argumentsJson)?.Caminho);

    public ChatTool ChatToolDefinition => ChatTool.CreateFunctionTool(
        functionName: Name,
        functionDescription: Description,
        functionParameters: BinaryData.FromString("""
        {
            "type": "object",
            "properties": {
                "path": {
                    "type": "string",
                    "description": "Caminho completo e absoluto do arquivo a editar."
                },
                "old_string": {
                    "type": "string",
                    "description": "O texto exato a substituir, como está no arquivo."
                },
                "new_string": {
                    "type": "string",
                    "description": "O texto que entra no lugar. Vazio apaga o trecho."
                },
                "replace_all": {
                    "type": "boolean",
                    "description": "Trocar TODAS as ocorrências. Padrão false, que exige trecho único."
                }
            },
            "required": ["path", "old_string", "new_string"]
        }
        """)
    );

    /// <summary>
    /// Recusa antes do portão humano o que não pode dar certo: arquivo que não existe, trecho
    /// ausente, ou trecho repetido sem <c>replace_all</c>. Ver <see cref="ITool.Validar"/>.
    /// </summary>
    public string? Validar(string argumentsJson)
    {
        try
        {
            var a = Ler(argumentsJson);
            if (a == null) return null;

            if (!File.Exists(a.Caminho))
                return PreVooDeCaminho.Conferir($"\"{a.Caminho}\"")
                       ?? $"ERRO: '{a.Caminho}' não é um arquivo.";

            if (a.De.Length == 0)
                return "ERRO: 'old_string' vazio. Para criar um arquivo use a ferramenta "
                       + $"'{Ferramentas.Gravar}'.";

            if (a.De == a.Para)
                return "ERRO: 'old_string' e 'new_string' são iguais. Nada a fazer.";

            string texto = File.ReadAllText(a.Caminho);
            int quantas = Contar(texto, a.De);

            if (quantas == 0)
                return $"ERRO: o trecho não existe em '{a.Caminho}'. Leia o arquivo com "
                       + $"'{Ferramentas.Ler}' e copie o texto exato, com a indentação. "
                       + $"Procurado: {Previa(a.De)}";

            if (quantas > 1 && !a.Todas)
                return $"ERRO: o trecho aparece {quantas} vezes em '{a.Caminho}'. Editar a "
                       + "primeira seria mexer no lugar errado sem avisar. Inclua as linhas em "
                       + "volta para deixá-lo único, ou passe replace_all=true para trocar todas.";

            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public CommandConfirmationContext? BuildConfirmationContext(string argumentsJson, int userLevel)
    {
        var a = Ler(argumentsJson);
        if (a == null) return null;

        string resolvido;
        try { resolvido = Path.GetFullPath(a.Caminho); }
        catch { return null; }

        return new CommandConfirmationContext
        {
            Tool = Name,
            Command = "EDITAR " + resolvido,

            // O card mostra o antes e o depois: autorizar uma edição sem ver o que muda seria
            // autorizar no escuro. ScriptBody é o campo que o card já renderiza como corpo.
            ScriptBody = $"- {Previa(a.De)}\n+ {Previa(a.Para)}",
            Level = userLevel,
            DenylistHit = false,
            DenylistReason = ""
        };
    }

    public Task<string> ExecuteAsync(string argumentsJson, int userLevel = 1)
    {
        var a = Ler(argumentsJson);
        if (a == null) return Task.FromResult("ERRO: argumentos ilegíveis ou incompletos.");

        try
        {
            string texto = File.ReadAllText(a.Caminho);
            int quantas = Contar(texto, a.De);

            // As mesmas guardas do pré-voo, de novo. O arquivo pode ter mudado entre a
            // validação e o clique de autorizar — e são segundos ou horas de intervalo.
            if (quantas == 0)
                return Task.FromResult($"ERRO: o trecho não existe mais em '{a.Caminho}'. "
                                       + "O arquivo mudou desde a leitura.");

            if (quantas > 1 && !a.Todas)
                return Task.FromResult($"ERRO: o trecho aparece {quantas} vezes em '{a.Caminho}'.");

            string novo = a.Todas
                ? texto.Replace(a.De, a.Para, StringComparison.Ordinal)
                : Substituir(texto, a.De, a.Para);

            Console.WriteLine($"[TOOL: {Name}] {a.Caminho} — {quantas} troca(s).");

            File.WriteAllText(a.Caminho, novo);

            return Task.FromResult(
                $"SUCESSO: {(a.Todas ? quantas : 1)} troca(s) em '{a.Caminho}'.");
        }
        catch (Exception ex)
        {
            return Task.FromResult($"ERRO ao editar: {ex.Message}");
        }
    }

    // ─────────────────────────────────────────────────────────────────────────

    private sealed record Argumentos(string Caminho, string De, string Para, bool Todas);

    private static Argumentos? Ler(string argumentsJson)
    {
        try
        {
            var args = JsonSerializer.Deserialize<JsonElement>(argumentsJson);
            if (args.ValueKind != JsonValueKind.Object) return null;

            string caminho = PathArgumentRepair.Normalize(Texto(args, "path"));
            if (caminho.Length == 0) return null;

            return new Argumentos(
                caminho,
                Texto(args, "old_string") ?? "",
                Texto(args, "new_string") ?? "",
                args.TryGetProperty("replace_all", out var r)
                && r.ValueKind == JsonValueKind.True);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Texto(JsonElement args, string nome) =>
        args.TryGetProperty(nome, out var campo) && campo.ValueKind == JsonValueKind.String
            ? campo.GetString()
            : null;

    /// <summary>Quantas vezes o trecho aparece. Ordinal: comparação byte a byte, sem cultura.</summary>
    public static int Contar(string texto, string trecho)
    {
        if (string.IsNullOrEmpty(trecho)) return 0;

        int quantas = 0;
        int i = texto.IndexOf(trecho, StringComparison.Ordinal);

        while (i >= 0)
        {
            quantas++;
            i = texto.IndexOf(trecho, i + trecho.Length, StringComparison.Ordinal);
        }

        return quantas;
    }

    /// <summary>Troca só a primeira ocorrência. <c>string.Replace</c> troca todas.</summary>
    public static string Substituir(string texto, string de, string para)
    {
        int i = texto.IndexOf(de, StringComparison.Ordinal);
        return i < 0 ? texto : texto.Substring(0, i) + para + texto.Substring(i + de.Length);
    }

    /// <summary>Trecho encurtado e numa linha só, para caber em mensagem de erro e em card.</summary>
    public static string Previa(string? trecho)
    {
        string t = (trecho ?? "").Replace("\r", "").Replace("\n", "⏎");

        return t.Length <= TetoDaPrevia ? t : t.Substring(0, TetoDaPrevia) + "…";
    }
}
