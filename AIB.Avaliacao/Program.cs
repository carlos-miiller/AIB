using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AIB.Services;
using AIB.Services.Agent;
using AIB.Services.Ai;

// ─────────────────────────────────────────────────────────────────────────────
// Avaliação do prompt.
//
//   dotnet run --project AIB.Avaliacao -- <rótulo> [pasta de saída]
//
// Para cada caso: monta o primeiro envio de uma conversa NOVA pelo mesmo caminho do app
// (ConversationService.MontarPrimeiroEnvio), manda ao provider configurado e confere o que
// voltou. NENHUMA ferramenta é executada — a chamada que o modelo pede é só anotada. Por isso
// "crie um arquivo" aqui não cria nada.
//
// Lê as configurações, a alma, as skills e o diário de triagem reais do usuário, e não grava
// nada em ~/.AIB: a memória da conversa vai para uma pasta temporária apagada no fim.
// ─────────────────────────────────────────────────────────────────────────────

Console.OutputEncoding = Encoding.UTF8;

//   Opções:  --casos escrever,email     só esses casos
//            --repeticoes 3             cada caso N vezes. Uma amostra só é ruído: em 14/09 o
//                                       mesmo prompt passou e falhou no mesmo caso.

string? Opcao(string nome)
{
    int i = Array.IndexOf(args, nome);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

string rotulo = args.Length > 0 && !args[0].StartsWith("--") ? args[0] : "sem-rotulo";
string pastaDeSaida = args.Length > 1 && !args[1].StartsWith("--")
    ? args[1]
    : Path.Combine(AppContext.BaseDirectory, "resultados");

var soCasos = (Opcao("--casos") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
int repeticoes = int.TryParse(Opcao("--repeticoes"), out int rep) && rep > 0 ? rep : 1;
Directory.CreateDirectory(pastaDeSaida);

string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
string memoriaTemporaria = Path.Combine(Path.GetTempPath(), "aib-avaliacao-" + Guid.NewGuid().ToString("N"));

var settingsService = new SettingsService();
var settings = settingsService.LoadSettings();

var registro = new ToolRegistry(confirmationPrompt: null, settingsService);
var contador = new TokenCounter();
var fabrica = new ChatProviderFactory(new HttpClient { Timeout = Timeout.InfiniteTimeSpan }, new RegexToolCallHealer());
var laco = new AgentLoop(registro, fabrica, settingsService, contador);
var conversa = new ConversationService(settingsService, registro, laco, contador, fabrica, memoriaTemporaria);

string persona = string.IsNullOrWhiteSpace(settings.ActiveCharacter) ? "AIB" : settings.ActiveCharacter.Trim();
var agora = DateTime.Now;
var ptBr = new CultureInfo("pt-BR");

// O mesmo resumo que vai no contexto do momento: é contra ele que a resposta de contagem é
// conferida.
string resumoDoVigia = ConversationService.EstadoDoVigia(settings, new AIB.Services.Mail.DiarioDeTriagem(), agora);

var casos = new List<Caso>
{
    new("saudacao", "oi",
        r => r.Chamadas.Count == 0 && r.Texto.Trim().Length > 0
            ? Passou() : Falhou("devia responder em texto, sem ferramenta")),

    new("identidade", "quem é você?",
        r => r.Texto.Contains(persona, StringComparison.OrdinalIgnoreCase)
             && (persona == "AIB" || !Regex.IsMatch(r.Texto, @"\bsou (o |a )?AIB\b", RegexOptions.IgnoreCase))
            ? Passou() : Falhou($"devia se apresentar como {persona}, sem se dizer outro")),

    new("data", "que dia é hoje?",
        r => MencionaHoje(r, agora, ptBr)
            ? Passou() : Falhou($"devia dizer {agora:dd/MM/yyyy} (ou consultar o relógio)")),

    new("conta", "quanto é 17 vezes 23?",
        r => r.Texto.Contains("391") || r.Chamou("shell")
            ? Passou() : Falhou("devia dar 391")),

    new("escrever", $@"crie o arquivo {home}\Desktop\teste-aib.txt com o texto olá",
        r => r.ChamouCom("write", "teste-aib.txt")
            ? Passou() : Falhou("devia chamar write com o caminho")),

    new("ler", $@"leia o arquivo {home}\CPAPS\AIB\README.md",
        r => r.ChamouCom("read", "README.md")
            ? Passou() : Falhou("devia chamar read com o caminho")),

    new("listar", $@"quais arquivos .md existem em {home}\CPAPS\AIB?",
        r => r.ChamouCom("glob", ".md")
            ? Passou() : Falhou("devia chamar glob")),

    new("procurar", $@"em quais arquivos .cs de {home}\CPAPS\AIB aparece a palavra TODO?",
        r => r.ChamouCom("grep", "TODO")
            ? Passou() : Falhou("devia chamar grep com TODO")),

    new("planilha", $@"o que tem na planilha {home}\Downloads\Listas de Ramais.xlsx?",
        r => r.ChamouCom("skill", "ler-planilha") && r.ChamouCom("skill", "-Path")
            ? Passou() : Falhou("devia chamar skill ler-planilha com -Path")),

    // Contagem de hoje pode sair do resumo do vigia — é para isso que ele existe (EstadoDoVigia).
    // Critério revisto em 14/09, depois da ablação: sem o resumo o modelo INVENTOU números;
    // com ele, respondeu certo e sem a ida de ~45s à ferramenta. A resposta em texto só passa
    // se bater com o resumo real.
    new("email", "tem algum e-mail urgente?",
        r => r.Chamou("mail") || RespondeuPeloResumo(r, resumoDoVigia)
            ? Passou() : Falhou("devia chamar mail ou responder de acordo com o resumo do vigia")),

    new("email-detalhe", "de quem são os e-mails urgentes desta semana?",
        r => r.Chamou("mail")
            ? Passou() : Falhou("remetente e outro período só a ferramenta sabe: devia chamar mail")),
};

if (soCasos.Length > 0)
    casos = casos.Where(c => soCasos.Contains(c.Nome)).ToList();

Console.WriteLine($"[AVALIAÇÃO] {rotulo} · {settings.AiProvider} {settings.ModelName} · persona {persona} · {casos.Count} casos × {repeticoes}");

var provider = fabrica.GetProvider(settings);
var resultados = new List<(Caso Caso, Resultado R, (bool Ok, string Motivo) Veredito)>();

try
{
    // Aquecimento fora da conta: carrega o modelo e o prefixo, como o app faz ao abrir. O
    // tempo dele é o custo FRIO do prompt, e vai para o relatório à parte.
    Console.WriteLine("[AVALIAÇÃO] aquecendo (não conta)...");
    var aquecimento = await RodarAsync(conversa.MontarPrimeiroEnvio("oi"), provider);
    Console.WriteLine($"[AVALIAÇÃO] aquecido: prompt {aquecimento.PromptTokens} tok, prefill frio {aquecimento.PrefillMs / 1000:0.0}s, total {aquecimento.TotalMs / 1000:0.0}s");

    foreach (var caso in casos)
    for (int k = 1; k <= repeticoes; k++)
    {
        var rodada = repeticoes == 1 ? caso : caso with { Nome = $"{caso.Nome}#{k}" };
        Console.WriteLine($"[AVALIAÇÃO] {rodada.Nome}: {caso.Fala}");
        var r = await RodarAsync(conversa.MontarPrimeiroEnvio(caso.Fala), provider);
        var veredito = r.Erro != null ? (false, "erro: " + r.Erro) : caso.Conferir(r);
        resultados.Add((rodada, r, veredito));
        Console.WriteLine($"[AVALIAÇÃO]   {(veredito.Item1 ? "PASSOU" : "FALHOU")} · {r.Resumo()} · {r.TotalMs / 1000:0.0}s");
    }

    string md = Relatorio(rotulo, settings, persona, aquecimento, resultados, conversa.SimularPrimeiroEnvio("oi"), contador);
    string carimbo = DateTime.Now.ToString("yyyyMMdd-HHmm");
    string caminho = Path.Combine(pastaDeSaida, $"{rotulo}-{carimbo}.md");
    File.WriteAllText(caminho, md, new UTF8Encoding(false));
    Console.WriteLine($"[AVALIAÇÃO] relatório: {caminho}");
}
finally
{
    try { Directory.Delete(memoriaTemporaria, true); } catch { }
}

return;

// ─────────────────────────────────────────────────────────────────────────────

static bool RespondeuPeloResumo(Resultado r, string resumo)
{
    if (r.Chamadas.Count > 0) return false;

    var m = Regex.Match(resumo, @"(\d+) de urgência máxima");
    if (!m.Success) return false; // sem resumo de hoje, não há de onde responder

    string t = r.Texto.ToLowerInvariant();
    return m.Groups[1].Value == "0"
        ? Regex.IsMatch(t, @"\b(não|nenhum|nenhuma)\b")
        : t.Contains(m.Groups[1].Value);
}

static (bool, string) Passou() => (true, "");
static (bool, string) Falhou(string motivo) => (false, motivo);

static bool MencionaHoje(Resultado r, DateTime agora, CultureInfo pt)
{
    if (r.ChamouCom("shell", "Date")) return true;

    string t = r.Texto.ToLowerInvariant();
    string mes = pt.DateTimeFormat.GetMonthName(agora.Month).ToLowerInvariant();

    return t.Contains(agora.ToString("dd/MM"))
        || t.Contains(agora.ToString("d/M"))
        || t.Contains(agora.ToString("yyyy-MM-dd"))
        || Regex.IsMatch(t, $@"\b0?{agora.Day}\s+de\s+{mes}\b");
}

static async Task<Resultado> RodarAsync(ConversationService.PrimeiroEnvio envio, IChatProvider provider)
{
    var r = new Resultado();
    var texto = new StringBuilder();
    var chamadas = new Dictionary<string, (StringBuilder Nome, StringBuilder Args)>();
    var relogio = Stopwatch.StartNew();

    // Teto por caso: raciocínio desgovernado em CPU passa de minutos, e um caso preso não pode
    // segurar a avaliação inteira.
    using var teto = new CancellationTokenSource(TimeSpan.FromMinutes(12));

    try
    {
        await foreach (var chunk in provider.StreamAsync(envio.Mensagens, envio.Ferramentas, envio.Opcoes, teto.Token))
        {
            switch (chunk)
            {
                case StreamChunk.TextDelta d when d.Channel == TextChannel.Final:
                    texto.Append(d.Text);
                    break;

                case StreamChunk.TextDelta d:
                    r.RaciocinioChars += d.Text.Length;
                    break;

                case StreamChunk.ToolCallDelta c:
                    if (!chamadas.TryGetValue(c.CallKey, out var acc))
                        chamadas[c.CallKey] = acc = (new StringBuilder(), new StringBuilder());
                    if (c.FunctionName != null) acc.Nome.Append(c.FunctionName);
                    if (c.ArgumentsJsonFragment != null) acc.Args.Append(c.ArgumentsJsonFragment);
                    break;

                case StreamChunk.Usage u:
                    r.PromptTokens = u.PromptEvalCount ?? r.PromptTokens;
                    r.GeracaoTokens = u.EvalCount ?? r.GeracaoTokens;
                    r.PrefillMs = u.PromptEvalMillis ?? r.PrefillMs;
                    break;
            }
        }
    }
    catch (Exception ex)
    {
        r.Erro = ex is OperationCanceledException ? "passou de 12 minutos" : ex.Message;
    }

    r.TotalMs = relogio.Elapsed.TotalMilliseconds;
    r.Texto = texto.ToString();
    r.Chamadas = chamadas.Values.Select(c => (c.Nome.ToString(), c.Args.ToString())).ToList();
    return r;
}

static string Relatorio(
    string rotulo, UserAppSettings settings, string persona, Resultado aquecimento,
    List<(Caso Caso, Resultado R, (bool Ok, string Motivo) Veredito)> resultados,
    string corpoDoOi, TokenCounter contador)
{
    var sb = new StringBuilder();
    int passaram = resultados.Count(x => x.Veredito.Ok);

    var comTexto = resultados.Where(x => x.R.Texto.Trim().Length > 0).ToList();
    int assinados = persona == "Kai" ? comTexto.Count(x => x.R.Texto.TrimStart().StartsWith("Kai online.", StringComparison.Ordinal)) : -1;

    sb.AppendLine($"# Avaliação do prompt · {rotulo}");
    sb.AppendLine();
    sb.AppendLine($"{DateTime.Now:yyyy-MM-dd HH:mm} · {settings.AiProvider} `{settings.ModelName}` · persona **{persona}** · raciocínio {(settings.ModelThinking ? "o modelo decide" : "desligado")}");
    sb.AppendLine();
    sb.AppendLine($"- **Casos que passaram: {passaram}/{resultados.Count}**");
    if (assinados >= 0)
        sb.AppendLine($"- Assinatura `Kai online.` nas respostas em texto: {assinados}/{comTexto.Count}");
    sb.AppendLine($"- Corpo do primeiro envio (\"oi\"): {corpoDoOi.Length} caracteres");
    sb.AppendLine($"- Prompt medido pelo Ollama: {aquecimento.PromptTokens} tokens · prefill frio {aquecimento.PrefillMs / 1000:0.0}s");
    sb.AppendLine($"- Tempo total dos casos: {resultados.Sum(x => x.R.TotalMs) / 1000:0}s");
    sb.AppendLine();
    sb.AppendLine("| caso | resultado | pediu | prefill | geração | raciocínio | total |");
    sb.AppendLine("|---|---|---|---|---|---|---|");

    foreach (var (caso, r, v) in resultados)
    {
        string pediu = r.Chamadas.Count == 0 ? "texto" : string.Join(", ", r.Chamadas.Select(c => c.Nome));
        sb.AppendLine($"| {caso.Nome} | {(v.Ok ? "✅" : "❌ " + v.Motivo)} | {pediu} | {r.PrefillMs / 1000:0.0}s | {r.GeracaoTokens} tok | {r.RaciocinioChars} ch | {r.TotalMs / 1000:0.0}s |");
    }

    sb.AppendLine();
    sb.AppendLine("## Respostas");

    foreach (var (caso, r, _) in resultados)
    {
        sb.AppendLine();
        sb.AppendLine($"### {caso.Nome} — \"{caso.Fala}\"");
        foreach (var (nome, args) in r.Chamadas)
            sb.AppendLine($"- chamou `{nome}` `{Cortar(args, 400)}`");
        if (r.Texto.Trim().Length > 0)
        {
            sb.AppendLine();
            sb.AppendLine("> " + Cortar(r.Texto.Trim(), 600).Replace("\n", "\n> "));
        }
        if (r.Erro != null) sb.AppendLine($"- erro: {r.Erro}");
    }

    return sb.ToString();
}

static string Cortar(string s, int n) => s.Length <= n ? s : s[..n] + "…";

sealed record Caso(string Nome, string Fala, Func<Resultado, (bool, string)> Conferir);

sealed class Resultado
{
    public string Texto { get; set; } = "";
    public List<(string Nome, string Args)> Chamadas { get; set; } = new();
    public int RaciocinioChars { get; set; }
    public int PromptTokens { get; set; }
    public int GeracaoTokens { get; set; }
    public double PrefillMs { get; set; }
    public double TotalMs { get; set; }
    public string? Erro { get; set; }

    public bool Chamou(string nome) => Chamadas.Any(c => c.Nome == nome);

    public bool ChamouCom(string nome, string trecho) =>
        Chamadas.Any(c => c.Nome == nome && c.Args.Contains(trecho, StringComparison.OrdinalIgnoreCase));

    public string Resumo() => Chamadas.Count > 0
        ? string.Join(", ", Chamadas.Select(c => $"{c.Nome}({(c.Args.Length > 80 ? c.Args[..80] + "…" : c.Args)})"))
        : "texto: " + (Texto.Length > 80 ? Texto[..80].Replace("\n", " ") + "…" : Texto.Replace("\n", " "));
}
