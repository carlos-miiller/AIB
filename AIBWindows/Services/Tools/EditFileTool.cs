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
/// <para>
/// O que NÃO é invariante é o fim de linha: o <c>read</c> entrega as linhas sem o <c>\r</c>, e
/// exigir que o trecho volte com ele tornava a ferramenta impossível de usar num arquivo CRLF.
/// Ver <see cref="Casar"/>.
/// </para>
/// </summary>
public sealed class EditFileTool : ITool
{
    /// <summary>Quanto do trecho aparece nas mensagens de erro e no card.</summary>
    private const int TetoDaPrevia = 200;

    public string Name => Ferramentas.Editar;

    public string Description =>
        "Troca um trecho dentro de um arquivo, preservando o resto. É a forma certa de mexer em "
        + "arquivo que já existe. 'old_string' precisa existir e ser único — inclua as linhas em "
        + "volta para desambiguar, ou 'replace_all' para trocar todas. Não cria arquivo (use "
        + "'write'). O fim de linha não precisa bater.";

    /// <summary>
    /// O mesmo nível do <c>write</c>: os dois mudam arquivo e passam pelo mesmo portão. Era 1, e
    /// quem estava no nível 1 podia editar o que não podia gravar.
    /// </summary>
    public int RequiredLevel => 2;

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
                    "description": "O trecho como o 'read' mostrou. Espaços e indentação contam; fim de linha não."
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
    /// Recusa antes do portão humano o que não pode dar certo: argumentos ilegíveis, arquivo que
    /// não existe, trecho ausente, ou trecho repetido sem <c>replace_all</c>. Ver
    /// <see cref="ITool.Validar"/>.
    /// </summary>
    public string? Validar(string argumentsJson)
    {
        try
        {
            // JSON ilegível não pode virar "ACESSO NEGADO: não foi possível descrever a
            // operação" lá na frente: quem lê isso conclui que o problema é permissão e vai
            // trocar de caminho, de ferramenta e de nível — nunca de sintaxe. Aconteceu.
            if (!ObjetoJson(argumentsJson))
                return "ERRO: argumentos ilegíveis. Envie um objeto JSON com 'path', "
                       + "'old_string' e 'new_string'.";

            var a = Ler(argumentsJson);
            if (a == null)
                return "ERRO: o parâmetro 'path' é obrigatório e não pode estar vazio.";

            if (!File.Exists(a.Caminho))
                return PreVooDeCaminho.Conferir($"\"{a.Caminho}\"")
                       ?? $"ERRO: '{a.Caminho}' não é um arquivo.";

            if (a.De.Length == 0)
                return "ERRO: 'old_string' vazio. Para criar um arquivo use a ferramenta "
                       + $"'{Ferramentas.Gravar}'.";

            if (a.De == a.Para)
                return "ERRO: 'old_string' e 'new_string' são iguais. Nada a fazer.";

            string texto = File.ReadAllText(a.Caminho);

            return Recusa(Casar(texto, a.De, a.Para), a);
        }
        catch (JsonException)
        {
            return "ERRO: argumentos ilegíveis. Envie um objeto JSON com 'path', 'old_string' "
                   + "e 'new_string'.";
        }
    }

    /// <summary>
    /// A recusa que corresponde ao casamento, ou <c>null</c> quando a troca pode acontecer.
    /// Usada pelo pré-voo e repetida na execução — o arquivo pode mudar entre uma e outra.
    /// </summary>
    private static string? Recusa(Casamento c, Argumentos a)
    {
        if (c.Quantas == 0)
            return $"ERRO: o trecho não existe em '{a.Caminho}'. Leia o arquivo com "
                   + $"'{Ferramentas.Ler}' e copie o texto exato, com a indentação. "
                   + $"Procurado: {Previa(a.De)}";

        // Trecho com LF num arquivo CRLF (ou o contrário): o ajuste só vale para trecho único,
        // porque uniformizar fim de linha em vários lugares de uma vez é estrago silencioso.
        if (c.Ajuste != null && c.Quantas > 1)
            return $"ERRO: o trecho aparece {c.Quantas} vezes em '{a.Caminho}', e o arquivo usa "
                   + $"{c.Ajuste} enquanto o trecho veio com o outro fim de linha. O ajuste "
                   + "automático só vale para trecho único: inclua as linhas em volta para "
                   + "deixá-lo único, ou edite uma linha por vez.";

        if (c.Quantas > 1 && !a.Todas)
            return $"ERRO: o trecho aparece {c.Quantas} vezes em '{a.Caminho}'. Editar a "
                   + "primeira seria mexer no lugar errado sem avisar. Inclua as linhas em "
                   + "volta para deixá-lo único, ou passe replace_all=true para trocar todas.";

        return null;
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
            // autorizar no escuro. Este comentário já afirmou que o card renderizava ScriptBody,
            // e nenhuma view o lia — o usuário autorizava vendo só o caminho. Agora o
            // ConfirmCardView mostra ScriptBody no bloco de prévia, quando há.
            ScriptBody = $"- {Previa(a.De)}\n+ {Previa(a.Para)}",
            Level = userLevel
        };
    }

    public Task<string> ExecuteAsync(string argumentsJson, int userLevel = 1)
    {
        var a = Ler(argumentsJson);
        if (a == null) return Task.FromResult("ERRO: argumentos ilegíveis ou incompletos.");

        try
        {
            string texto = File.ReadAllText(a.Caminho);
            var c = Casar(texto, a.De, a.Para);

            // As mesmas guardas do pré-voo, de novo. O arquivo pode ter mudado entre a
            // validação e o clique de autorizar — e são segundos ou horas de intervalo.
            if (c.Quantas == 0)
                return Task.FromResult($"ERRO: o trecho não existe mais em '{a.Caminho}'. "
                                       + "O arquivo mudou desde a leitura.");

            string? recusa = Recusa(c, a);
            if (recusa != null) return Task.FromResult(recusa);

            // Com fim de linha ajustado a troca é sempre a de um trecho único: é a guarda que
            // torna o ajuste seguro, e ela vale mesmo com replace_all.
            string novo = a.Todas && c.Ajuste == null
                ? texto.Replace(c.De, c.Para, StringComparison.Ordinal)
                : Substituir(texto, c.De, c.Para);

            Console.WriteLine($"[TOOL: {Name}] {a.Caminho} — {c.Quantas} troca(s).");

            File.WriteAllText(a.Caminho, novo);

            int trocas = a.Todas && c.Ajuste == null ? c.Quantas : 1;
            string nota = c.Ajuste == null
                ? ""
                : $" (o arquivo usa {c.Ajuste}; o trecho foi ajustado)";

            return Task.FromResult($"SUCESSO: {trocas} troca(s) em '{a.Caminho}'.{nota}");
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

    private static bool ObjetoJson(string argumentsJson)
    {
        try { return JsonSerializer.Deserialize<JsonElement>(argumentsJson).ValueKind == JsonValueKind.Object; }
        catch (JsonException) { return false; }
    }

    /// <summary>
    /// O resultado do casamento do trecho com o arquivo: quantas vezes ele aparece e com que
    /// texto a troca será feita.
    /// </summary>
    /// <param name="Ajuste">
    /// <c>null</c> quando o trecho casou como veio. "CRLF" ou "LF" quando foi preciso ajustar o
    /// fim de linha do trecho ao do arquivo — e então a troca é sempre de uma ocorrência só.
    /// </param>
    public readonly record struct Casamento(int Quantas, string De, string Para, string? Ajuste);

    /// <summary>
    /// Casa o trecho com o arquivo, tolerando fim de linha diferente.
    /// <para>
    /// O caso real: um <c>docker-compose.yml</c> do repositório do GLPI, em CRLF. O
    /// <c>read</c> entrega as linhas SEM o <c>\r</c>, o modelo copiou dali um trecho de seis
    /// linhas, e o <c>edit</c> respondeu duas vezes "o trecho não existe" — para um trecho que
    /// estava lá. Ele foi ao shell fazer <c>Format-Hex</c>, desistiu e regerou o arquivo inteiro
    /// com <c>write</c>, perdendo um comentário no caminho. A promessa "prefira sempre esta
    /// ferramenta a reescrever o arquivo" era impossível de cumprir.
    /// </para>
    /// <para>
    /// DUAS GUARDAS, e as duas existem para que o ajuste nunca corrompa nada:
    /// <list type="bullet">
    /// <item>o arquivo é UNIFORME — ou todo CRLF, ou todo LF. Arquivo de fim de linha misto não
    /// entra: ali adivinhar é uniformizar sem pedir;</item>
    /// <item>o trecho convertido casa EXATAMENTE uma vez. Mais de uma, e a troca vira recusa com
    /// o motivo dito — o ajuste é local, nunca uma varredura pelo arquivo.</item>
    /// </list>
    /// O resto do arquivo não é tocado: a conversão é do trecho procurado e do que entra no
    /// lugar, jamais do texto em volta. Normalizar o arquivo inteiro antes de comparar mudaria
    /// todas as linhas por uma edição de três — é o conserto que não se deve fazer.
    /// </para>
    /// </summary>
    public static Casamento Casar(string texto, string de, string para)
    {
        int quantas = Contar(texto, de);
        if (quantas > 0 || de.Length == 0) return new Casamento(quantas, de, para, null);

        // Primeira guarda: o arquivo tem de ser uniforme, e o trecho tem de mudar na conversão.
        string? ajuste = FimDeLinha(texto);
        if (ajuste == null) return new Casamento(0, de, para, null);

        string deAjustado = ajuste == "CRLF" ? ParaCrlf(de) : ParaLf(de);
        if (string.Equals(deAjustado, de, StringComparison.Ordinal))
            return new Casamento(0, de, para, null);

        int comAjuste = Contar(texto, deAjustado);
        if (comAjuste == 0) return new Casamento(0, de, para, null);

        // Segunda guarda fica com quem decide (Recusa): aqui o número é devolvido como é, para
        // que a mensagem possa dizer quantas vezes o trecho aparece de verdade.
        string paraAjustado = ajuste == "CRLF" ? ParaCrlf(para) : ParaLf(para);

        return new Casamento(comAjuste, deAjustado, paraAjustado, ajuste);
    }

    /// <summary>
    /// "CRLF" se o texto usa só <c>\r\n</c>, "LF" se usa só <c>\n</c>, <c>null</c> se mistura os
    /// dois (ou não tem quebra de linha nenhuma).
    /// </summary>
    private static string? FimDeLinha(string texto)
    {
        int enes = 0, pares = 0;

        for (int i = 0; i < texto.Length; i++)
        {
            if (texto[i] != '\n') continue;
            enes++;
            if (i > 0 && texto[i - 1] == '\r') pares++;
        }

        if (enes == 0) return null;
        return pares == enes ? "CRLF" : pares == 0 ? "LF" : null;
    }

    private static string ParaLf(string t) => t.Replace("\r\n", "\n", StringComparison.Ordinal);

    private static string ParaCrlf(string t) => ParaLf(t).Replace("\n", "\r\n", StringComparison.Ordinal);

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
