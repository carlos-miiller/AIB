using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using OpenAI.Chat;

namespace AIB.Services.Tools;

/// <summary>
/// Gravar, editar e organizar arquivos numa ferramenta só, com <c>action</c> — e com LOTE.
/// <para>
/// POR QUE EXISTE. Eram três ferramentas (<c>write</c>, <c>edit</c>, <c>fs</c>), uma chamada
/// por arquivo. Visto no uso (29/10/2026): para organizar uma pasta, a agente moveu ~30
/// arquivos em ~30 voltas ao modelo, uma por vez — 98 de 98 respostas do dia vieram com uma
/// ferramenta só. Cada volta reenvia o contexto inteiro e espera a rede. Com <c>ops</c>, as 30
/// operações vão numa chamada, num cartão, e voltam numa resposta.
/// </para>
/// <para>
/// A LÓGICA NÃO MUDOU DE LUGAR. Cada operação é executada pelo motor que já existia
/// (<see cref="WriteFileTool"/>, <see cref="EditFileTool"/>, <see cref="FsTool"/>): o mesmo
/// pré-voo, o mesmo cartão, a mesma dispensa por pasta, o mesmo piso. Uma chamada de uma
/// operação só é, para o cartão, a memória e a tela, exatamente a chamada antiga
/// (ver <see cref="Simples"/> e <see cref="Abrir"/>). Os motores continuam registrados, fora do
/// schema: o histórico antigo tem <c>write</c>/<c>edit</c>/<c>fs</c>, e um modelo que imite o
/// histórico ainda passa pelo portão em vez de receber "ferramenta não encontrada".
/// </para>
/// <para>
/// O LOTE roda em ORDEM e PARA na primeira falha: "criar a pasta e mover para ela" só faz
/// sentido nessa ordem, e mover para uma pasta que não nasceu espalharia o resto. O cartão
/// mostra todas as operações; uma aprovação vale para o lote. Operação que depende de outra
/// do mesmo lote (o destino é a pasta que a anterior cria, o arquivo que a anterior gravou)
/// não tem como ser conferida no disco antes: o cartão a descreve pelos argumentos, e a
/// conferência acontece na hora de executar.
/// </para>
/// </summary>
public sealed class FileTool : ITool
{
    /// <summary>Teto de operações num lote. Acima disso o cartão deixa de ser lido.</summary>
    public const int TetoDoLote = 100;

    /// <summary>Teto da prévia de cada gravação/edição no cartão do lote.</summary>
    private const int TetoDaPrevia = 400;

    private readonly WriteFileTool _gravar;
    private readonly EditFileTool _editar;
    private readonly FsTool _fs;

    public FileTool() : this(new WriteFileTool(), new EditFileTool(), new FsTool()) { }

    public FileTool(WriteFileTool gravar, EditFileTool editar, FsTool fs)
    {
        _gravar = gravar;
        _editar = editar;
        _fs = fs;
    }

    public string Name => Ferramentas.Arquivo;

    public string Description =>
        "Grava, edita e organiza arquivos e pastas. action: write (cria, ou substitui TODO o "
        + "conteúdo; cria as pastas que faltam), edit (troca um trecho exato e único, ou "
        + "replace_all; o resto fica), mkdir, copy, move, rename, delete (vai para a Lixeira). "
        + "copy/move nunca sobrescrevem. Várias operações: mande todas em 'ops', numa chamada só.";

    public int RequiredLevel => 2;

    public bool RequiresConfirmation => true;

    public ChatTool ChatToolDefinition => ChatTool.CreateFunctionTool(
        functionName: Name,
        functionDescription: Description,
        functionParameters: BinaryData.FromString("""
        {
            "type": "object",
            "properties": {
                "action": { "type": "string", "enum": ["write", "edit", "mkdir", "copy", "move", "rename", "delete"] },
                "path": { "type": "string", "description": "Caminho absoluto." },
                "content": { "type": "string", "description": "write: o conteúdo inteiro." },
                "old_string": { "type": "string", "description": "edit: o trecho como o 'read' mostrou; espaços contam, fim de linha não." },
                "new_string": { "type": "string", "description": "edit: o que entra no lugar. Vazio apaga." },
                "replace_all": { "type": "boolean", "description": "edit: trocar todas as ocorrências." },
                "destination": { "type": "string", "description": "copy/move: caminho absoluto final." },
                "new_name": { "type": "string", "description": "rename: só o nome novo." },
                "ops": { "type": "array", "items": { "type": "object" }, "description": "Lote, no lugar de action/path: [{action,path,...}, ...], mesmos campos, executados em ordem; para na primeira falha." }
            }
        }
        """)
    );

    // ─────────────────────────────────────────────────────────────────────────
    // As operações
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Uma operação: o motor que a executa e os argumentos no formato dele.</summary>
    private sealed record Op(string Acao, ITool Motor, string Args);

    private static readonly string[] Acoes = { "write", "edit", "mkdir", "copy", "move", "rename", "delete" };

    /// <summary>O nome da ferramenta antiga que executa esta ação.</summary>
    public static string MotorDe(string acao) => acao switch
    {
        "write" => Ferramentas.Gravar,
        "edit" => Ferramentas.Editar,
        _ => Ferramentas.Arquivos
    };

    private ITool Motor(string acao) => acao switch
    {
        "write" => _gravar,
        "edit" => _editar,
        _ => _fs
    };

    /// <summary>
    /// As operações da chamada, ou a recusa. Um lugar só para o pré-voo, o cartão e a execução.
    /// Lote de UMA operação vira chamada simples: o cartão e a memória são os de sempre.
    /// </summary>
    private (IReadOnlyList<Op>? Ops, bool Lote, string? Recusa) Ler(string argumentsJson)
    {
        var (objetos, lote, recusa) = Objetos(argumentsJson);
        if (objetos == null) return (null, false, recusa);

        var ops = new List<Op>();
        for (int i = 0; i < objetos.Count; i++)
        {
            string acao = Texto(objetos[i], "action").Trim().ToLowerInvariant();
            if (!Acoes.Contains(acao))
                return (null, lote, (lote ? $"ERRO: operação {i + 1}: " : "ERRO: ")
                                    + "'action' tem de ser write, edit, mkdir, copy, move, rename ou delete.");

            ops.Add(new Op(acao, Motor(acao), objetos[i].GetRawText()));
        }

        return (ops, lote, null);
    }

    /// <summary>Os objetos de operação crus: o próprio argumento, ou os itens de 'ops'.</summary>
    private static (IReadOnlyList<JsonElement>? Objetos, bool Lote, string? Recusa) Objetos(string argumentsJson)
    {
        JsonElement args;
        try { args = JsonSerializer.Deserialize<JsonElement>(argumentsJson); }
        catch (JsonException) { return (null, false, Ilegivel); }

        if (args.ValueKind != JsonValueKind.Object) return (null, false, Ilegivel);

        if (!args.TryGetProperty("ops", out var ops) || ops.ValueKind == JsonValueKind.Null)
            return (new[] { args.Clone() }, false, null);

        if (ops.ValueKind != JsonValueKind.Array)
            return (null, true, "ERRO: 'ops' tem de ser uma lista de operações [{action,path,...}].");

        if (args.TryGetProperty("action", out var a) && a.ValueKind == JsonValueKind.String)
            return (null, true, "ERRO: mande 'ops' OU action/path, não os dois. No lote, cada operação leva o seu 'action'.");

        var itens = ops.EnumerateArray().Select(e => e.Clone()).ToList();
        if (itens.Count == 0) return (null, true, "ERRO: 'ops' vazio. Nada a fazer.");
        if (itens.Count > TetoDoLote)
            return (null, true, $"ERRO: {itens.Count} operações; o teto do lote é {TetoDoLote}. Divida em lotes menores.");

        for (int i = 0; i < itens.Count; i++)
            if (itens[i].ValueKind != JsonValueKind.Object)
                return (null, true, $"ERRO: operação {i + 1} não é um objeto {{action,path,...}}.");

        // Um item só não é lote: o cartão, a memória e a tela ficam os da operação.
        return (itens, itens.Count > 1, null);
    }

    private const string Ilegivel =
        "ERRO: argumentos ilegíveis. Envie um objeto JSON com 'action' e 'path', ou com 'ops'.";

    private static string Texto(JsonElement o, string nome) =>
        o.ValueKind == JsonValueKind.Object && o.TryGetProperty(nome, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? ""
            : "";

    // ─────────────────────────────────────────────────────────────────────────
    // Dependência dentro do lote
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Os caminhos que a operação toca, já absolutos: o alvo e, quando há, o destino.
    /// Caminho ilegível fica de fora — quem recusa é o motor.
    /// </summary>
    private static IReadOnlyList<string> Caminhos(Op op)
    {
        var o = JsonSerializer.Deserialize<JsonElement>(op.Args);
        var saida = new List<string>();

        string? Cheio(string bruto)
        {
            string p = PathArgumentRepair.Normalize(bruto);
            if (p.Length == 0) return null;
            try { return Path.GetFullPath(p).TrimEnd('\\', '/'); }
            catch { return null; }
        }

        if (Cheio(Texto(o, "path")) is string alvo)
        {
            saida.Add(alvo);
            if (op.Acao == "rename" && Texto(o, "new_name").Trim() is { Length: > 0 } nome)
                saida.Add(Path.Combine(Path.GetDirectoryName(alvo) ?? "", nome));
        }

        if (op.Acao is "copy" or "move" && Cheio(Texto(o, "destination")) is string destino)
            saida.Add(destino);

        return saida;
    }

    /// <summary>
    /// Para cada operação, se ela mexe em algo que uma ANTERIOR do lote mexeu: o mesmo caminho,
    /// um que está dentro dele, ou a pasta que o contém. Essa não se confere no disco antes de
    /// executar — o disco ainda não é o que ela vai encontrar.
    /// </summary>
    private static bool[] Dependentes(IReadOnlyList<Op> ops)
    {
        var tocados = new List<string>();
        var dependente = new bool[ops.Count];

        for (int i = 0; i < ops.Count; i++)
        {
            var meus = Caminhos(ops[i]);
            dependente[i] = meus.Any(c => tocados.Any(t => Relacionados(c, t)));
            tocados.AddRange(meus);
        }

        return dependente;
    }

    private static bool Relacionados(string a, string b) =>
        string.Equals(a, b, StringComparison.OrdinalIgnoreCase)
        || a.StartsWith(b + "\\", StringComparison.OrdinalIgnoreCase)
        || b.StartsWith(a + "\\", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A linha do cartão de uma operação que depende de outra do lote: sai dos argumentos, sem
    /// olhar o disco (sem tamanho, sem "criar ou sobrescrever").
    /// </summary>
    private static string Prevista(Op op)
    {
        var o = JsonSerializer.Deserialize<JsonElement>(op.Args);
        string Cheio(string nome)
        {
            string p = PathArgumentRepair.Normalize(Texto(o, nome));
            try { return Path.GetFullPath(p).TrimEnd('\\', '/'); }
            catch { return p; }
        }

        return op.Acao switch
        {
            "write" => "GRAVAR " + Cheio("path"),
            "edit" => "EDITAR " + Cheio("path"),
            "mkdir" => "CRIAR PASTA " + Cheio("path"),
            "copy" => $"COPIAR {Cheio("path")} → {Cheio("destination")}",
            "move" => $"MOVER {Cheio("path")} → {Cheio("destination")}",
            "rename" => $"RENOMEAR {Cheio("path")} → {Texto(o, "new_name").Trim()}",
            _ => $"APAGAR {Cheio("path")}"
        };
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Pré-voo, dispensa, cartão e piso
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Recusa antes do cartão. No lote, a recusa diz QUAL operação, e nada roda: um lote que
    /// faria metade e pararia na outra metade por um erro previsível é pior que nenhum.
    /// </summary>
    public string? Validar(string argumentsJson)
    {
        var (ops, lote, recusa) = Ler(argumentsJson);
        if (ops == null) return recusa;

        if (!lote) return ops[0].Motor.Validar(ops[0].Args);

        var dependente = Dependentes(ops);
        for (int i = 0; i < ops.Count; i++)
        {
            if (dependente[i]) continue;
            if (ops[i].Motor.Validar(ops[i].Args) is string erro)
                return $"ERRO: operação {i + 1} de {ops.Count} ({ops[i].Acao}): {SemPrefixo(erro)} Nada do lote foi feito.";
        }

        return null;
    }

    /// <summary>"ERRO: x" vira "x": no lote o prefixo já vem na frente.</summary>
    private static string SemPrefixo(string erro) =>
        erro.StartsWith("ERRO: ", StringComparison.Ordinal) ? erro["ERRO: ".Length..] : erro;

    /// <summary>
    /// Dispensa o cartão só se TODAS as operações seriam dispensadas sozinhas. Uma fora da pasta
    /// de confiança leva o lote inteiro ao cartão.
    /// </summary>
    public bool DispensaConfirmacao(string argumentsJson)
    {
        var (ops, _, _) = Ler(argumentsJson);
        return ops != null && ops.All(o => o.Motor.DispensaConfirmacao(o.Args));
    }

    public CommandConfirmationContext? BuildConfirmationContext(string argumentsJson, int userLevel)
    {
        var (ops, lote, _) = Ler(argumentsJson);
        if (ops == null) return null;

        if (!lote) return ops[0].Motor.BuildConfirmationContext(ops[0].Args, userLevel);

        var dependente = Dependentes(ops);
        var linhas = new StringBuilder();
        var previa = new StringBuilder();

        for (int i = 0; i < ops.Count; i++)
        {
            string linha;
            string corpo = "";

            if (dependente[i])
            {
                linha = Prevista(ops[i]);
            }
            else
            {
                var ctx = ops[i].Motor.BuildConfirmationContext(ops[i].Args, userLevel);
                if (ctx == null) return null;
                linha = ctx.Command;
                corpo = ctx.ScriptBody ?? "";
            }

            linhas.Append(i + 1).Append(". ").Append(linha).Append('\n');

            if (ops[i].Acao is "write" or "edit")
            {
                if (corpo.Length == 0 && ops[i].Acao == "write") corpo = Texto(JsonSerializer.Deserialize<JsonElement>(ops[i].Args), "content");
                if (corpo.Length > TetoDaPrevia) corpo = corpo[..TetoDaPrevia] + "\n...[prévia truncada]";
                previa.Append(i + 1).Append(". ").Append(linha).Append('\n').Append(corpo).Append("\n\n");
            }
        }

        return new CommandConfirmationContext
        {
            Tool = Name,
            Command = linhas.ToString().TrimEnd('\n'),
            ScriptBody = previa.ToString().TrimEnd('\n'),
            Level = userLevel,
            Cwd = Environment.CurrentDirectory
        };
    }

    /// <summary>As linhas de um cartão de lote, sem o "N. " da frente.</summary>
    public static IReadOnlyList<string> LinhasDoLote(string? comando) =>
        (comando ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => Regex.Replace(l, @"^\d+\. ", ""))
            .ToList();

    /// <summary>
    /// O piso de cada operação. Só o <c>fs</c> tem (apagar pasta com conteúdo, Nível 7), e ele
    /// lê a própria linha do cartão — no lote, uma linha por operação.
    /// </summary>
    public string? PisoTipado(CommandConfirmationContext contexto, int userLevel)
    {
        if (contexto.Tool != Name) return ((ITool)_fs).PisoTipado(contexto, userLevel);

        foreach (string linha in LinhasDoLote(contexto.Command))
            if (((ITool)_fs).PisoTipado(new CommandConfirmationContext { Command = linha }, userLevel) is string barrado)
                return barrado;

        return null;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Execução
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Sem autorização descrita não há o que executar. O registry nunca chama isto.</summary>
    public Task<string> ExecuteAsync(string argumentsJson, int userLevel = 1) =>
        Task.FromResult("ERRO: esta ferramenta só executa o que foi autorizado.");

    /// <summary>
    /// Uma operação: o motor dela, com o autorizado. Lote: em ordem, conferindo cada operação
    /// contra a linha do cartão, e parando na primeira falha. O resultado traz uma linha
    /// "[i/n] ..." por operação — é por ela que a memória sabe o que cada uma fez.
    /// </summary>
    public async Task<string> ExecutarAutorizadoAsync(string argumentsJson, int userLevel, CommandConfirmationContext? autorizado)
    {
        var (ops, lote, recusa) = Ler(argumentsJson);
        if (ops == null) return recusa!;

        if (!lote) return await ops[0].Motor.ExecutarAutorizadoAsync(ops[0].Args, userLevel, autorizado);

        if (autorizado == null) return "ERRO: sem autorização descrita, nada foi feito.";

        var linhas = LinhasDoLote(autorizado.Command);
        if (linhas.Count != ops.Count) return "ERRO: o lote não confere com o que foi autorizado. Nada foi feito.";

        var dependente = Dependentes(ops);
        var saida = new List<string>();
        int feitas = 0, falhou = -1;

        for (int i = 0; i < ops.Count; i++)
        {
            string resultado = await Executar(ops[i], linhas[i], dependente[i], userLevel);
            saida.Add($"[{i + 1}/{ops.Count}] {resultado}");

            if (Memory.ArtifactExtractor.Falhou(resultado)) { falhou = i; break; }
            feitas++;
        }

        for (int i = falhou + 1; falhou >= 0 && i < ops.Count; i++)
            saida.Add($"[{i + 1}/{ops.Count}] ERRO: não executada — a operação {falhou + 1} falhou antes.");

        string cabeca = falhou < 0
            ? $"SUCESSO: as {ops.Count} operações foram feitas."
            : $"ERRO: o lote parou na operação {falhou + 1} de {ops.Count}; {feitas} feita(s) antes dela, "
              + "que continuam feitas. Corrija e mande só o que faltou.";

        return cabeca + "\n" + string.Join("\n", saida);
    }

    private async Task<string> Executar(Op op, string autorizada, bool dependente, int userLevel)
    {
        try
        {
            // O disco agora é o que as anteriores deixaram: o pré-voo de verdade é aqui.
            if (op.Motor.Validar(op.Args) is string erro) return erro;

            var agora = op.Motor.BuildConfirmationContext(op.Args, userLevel);
            if (agora == null) return "ERRO: não foi possível descrever a operação. Não executada.";

            // Independente: tem de ser o que o cartão mostrou. Dependente: o cartão mostrou a
            // operação pelos argumentos, e são os mesmos argumentos.
            if (!dependente && !string.Equals(agora.Command, autorizada, StringComparison.Ordinal))
                return $"ERRO: o alvo mudou desde a autorização (era \"{autorizada}\", agora é \"{agora.Command}\"). Não executada.";

            return await op.Motor.ExecutarAutorizadoAsync(op.Args, userLevel, agora);
        }
        catch (Exception ex)
        {
            return $"ERRO ao executar '{op.Acao}': {ex.Message}";
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Para quem lê o histórico
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Uma chamada <c>file</c> de UMA operação vira a chamada antiga equivalente (nome do motor,
    /// mesmos argumentos). Lote e qualquer outra ferramenta voltam como vieram. É o que deixa a
    /// tela, os ícones e a memória iguais aos de antes para o caso comum.
    /// </summary>
    public static (string Nome, string Args) Simples(string nome, string argumentsJson)
    {
        if (nome != Ferramentas.Arquivo) return (nome, argumentsJson);

        var (objetos, lote, _) = Objetos(argumentsJson);
        if (objetos == null || lote) return (nome, argumentsJson);

        string acao = Texto(objetos[0], "action").Trim().ToLowerInvariant();
        return Acoes.Contains(acao) ? (MotorDe(acao), objetos[0].GetRawText()) : (nome, argumentsJson);
    }

    /// <summary>
    /// A chamada aberta nas chamadas antigas equivalentes, cada uma com o SEU resultado.
    /// <para>
    /// Lote: uma por operação, com a linha "[i/n]" dela. Operação sem linha (recusa no cartão,
    /// pré-voo, piso, negação) recebe o resultado inteiro: nada do lote rodou, e a recusa vale
    /// para todas. Outra ferramenta, ou uma operação só: a própria chamada.
    /// </para>
    /// </summary>
    public static IReadOnlyList<(string Nome, string Args, string Resultado)> Abrir(
        string nome, string argumentsJson, string resultado)
    {
        if (nome != Ferramentas.Arquivo) return new[] { (nome, argumentsJson, resultado) };

        var (objetos, lote, _) = Objetos(argumentsJson);
        if (objetos == null) return new[] { (nome, argumentsJson, resultado) };

        if (!lote)
        {
            var (n, a) = Simples(nome, argumentsJson);
            return new[] { (n, a, resultado) };
        }

        var porOp = new Dictionary<int, string>();
        foreach (Match m in Regex.Matches(resultado ?? "", @"^\[(\d+)/\d+\] (.*)$", RegexOptions.Multiline))
            porOp[int.Parse(m.Groups[1].Value)] = m.Groups[2].Value.TrimEnd('\r');

        var saida = new List<(string, string, string)>();
        for (int i = 0; i < objetos.Count; i++)
        {
            string acao = Texto(objetos[i], "action").Trim().ToLowerInvariant();
            if (!Acoes.Contains(acao)) continue;
            saida.Add((MotorDe(acao), objetos[i].GetRawText(),
                porOp.TryGetValue(i + 1, out var r) ? r : resultado ?? ""));
        }

        return saida;
    }

    /// <summary>Quantas operações a chamada tem (1 fora do lote, 0 se ilegível).</summary>
    public static int Operacoes(string argumentsJson)
    {
        var (objetos, _, _) = Objetos(argumentsJson);
        return objetos?.Count ?? 0;
    }
}
