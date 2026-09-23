using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using OpenAI.Chat;

namespace AIB.Services.Tools;

/// <summary>
/// Operações de arquivo e pasta que não mexem em conteúdo: criar pasta, copiar, mover, renomear
/// e apagar. UMA ferramenta com <c>action</c>, e não cinco: cada ferramenta é schema pago em toda
/// requisição, e cinco custariam o triplo pelo mesmo poder.
/// <para>
/// POR QUE EXISTE, MEDIDO (ideias_futuras/07). Nas 49 sessões gravadas o shell foi 56% das
/// chamadas, e parte da fuga era falta de verbo: 10 <c>New-Item -ItemType Directory</c>, 6
/// apagamentos, 8 cópias ou movimentos. Cada um passou por uma linha de PowerShell que o app só
/// entendia por engenharia reversa (<see cref="Memory.ComandoDeShell"/>,
/// <see cref="Memory.ComandoQueApaga"/>) — e foi uma linha dessas, copiada da memória, que apagou
/// duas vezes a mesma pasta de trabalho.
/// </para>
/// <para>
/// O GANHO É O CARTÃO. Aqui o app sabe o que vai acontecer ANTES de acontecer: o cartão diz
/// "APAGAR PASTA X (14 arquivos, 320 KB)", e não uma linha de comando para o usuário decifrar.
/// </para>
/// <para>
/// APAGAR VAI PARA A LIXEIRA. Pelo shell, <c>Remove-Item</c> apaga de vez. Aqui o que sai vai
/// para a Lixeira do Windows, e um engano do modelo continua recuperável.
/// </para>
/// </summary>
public sealed class FsTool : ITool
{
    public string Name => Ferramentas.Arquivos;

    public string Description =>
        "Cria pasta, copia, move, renomeia ou apaga arquivo e pasta; apagar manda para a "
        + "Lixeira. Não lê, não lista e não grava conteúdo (read, glob, write). Nunca "
        + "sobrescreve: destino que existe é recusado.";

    public int RequiredLevel => 2;

    public bool RequiresConfirmation => true;

    public ChatTool ChatToolDefinition => ChatTool.CreateFunctionTool(
        functionName: Name,
        functionDescription: Description,
        functionParameters: BinaryData.FromString("""
        {
            "type": "object",
            "properties": {
                "action": { "type": "string", "enum": ["mkdir", "copy", "move", "rename", "delete"] },
                "path": { "type": "string", "description": "Caminho absoluto do alvo." },
                "destination": { "type": "string", "description": "copy/move: caminho absoluto final." },
                "new_name": { "type": "string", "description": "rename: só o nome novo." }
            },
            "required": ["action", "path"]
        }
        """)
    );

    // ─────────────────────────────────────────────────────────────────────────
    // Argumentos
    // ─────────────────────────────────────────────────────────────────────────

    private sealed record Pedido(string Acao, string Alvo, string? Destino);

    /// <summary>
    /// Lê e resolve os argumentos, ou devolve a recusa. Um lugar só: o pré-voo, o cartão e a
    /// execução não podem entender o mesmo pedido de jeitos diferentes.
    /// </summary>
    private static (Pedido? Pedido, string? Recusa) Ler(string argumentsJson)
    {
        JsonElement args;
        try { args = JsonSerializer.Deserialize<JsonElement>(argumentsJson); }
        catch (JsonException) { return (null, Ilegivel); }

        if (args.ValueKind != JsonValueKind.Object) return (null, Ilegivel);

        string acao = Texto(args, "action").ToLowerInvariant();
        string bruto = PathArgumentRepair.Normalize(Texto(args, "path"));

        if (acao is not ("mkdir" or "copy" or "move" or "rename" or "delete"))
            return (null, "ERRO: 'action' tem de ser mkdir, copy, move, rename ou delete.");

        if (bruto.Length == 0) return (null, "ERRO: o parâmetro 'path' é obrigatório.");

        string alvo;
        try { alvo = Path.GetFullPath(bruto).TrimEnd('\\', '/'); }
        catch { return (null, $"ERRO: '{bruto}' não é um caminho válido."); }

        if (!Path.IsPathRooted(bruto))
            return (null, $"ERRO: '{bruto}' não é caminho absoluto. Passe o caminho completo, com a unidade.");

        string? destino = null;

        if (acao is "copy" or "move")
        {
            string d = PathArgumentRepair.Normalize(Texto(args, "destination"));
            if (d.Length == 0) return (null, $"ERRO: '{acao}' precisa de 'destination'.");
            if (!Path.IsPathRooted(d)) return (null, $"ERRO: 'destination' tem de ser caminho absoluto.");
            try { destino = Path.GetFullPath(d).TrimEnd('\\', '/'); }
            catch { return (null, $"ERRO: '{d}' não é um caminho válido."); }
        }
        else if (acao == "rename")
        {
            string nome = Texto(args, "new_name").Trim();
            if (nome.Length == 0) return (null, "ERRO: 'rename' precisa de 'new_name'.");
            if (nome.IndexOfAny(new[] { '\\', '/', ':' }) >= 0 || nome is "." or "..")
                return (null, "ERRO: 'new_name' é só o nome, sem pasta. Para mudar de pasta use 'move'.");
            if (nome.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                return (null, $"ERRO: '{nome}' tem caractere que o Windows não aceita em nome.");
            destino = Path.Combine(Path.GetDirectoryName(alvo) ?? "", nome);
        }

        return (new Pedido(acao, alvo, destino), null);
    }

    private const string Ilegivel =
        "ERRO: argumentos ilegíveis. Envie um objeto JSON com 'action' e 'path'.";

    private static string Texto(JsonElement args, string nome) =>
        args.TryGetProperty(nome, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    // ─────────────────────────────────────────────────────────────────────────
    // Pré-voo
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Recusa antes do cartão o que não pode dar certo ou não deve ser perguntado: alvo que não
    /// existe, destino que já existe (esta ferramenta nunca sobrescreve), pasta copiada para
    /// dentro de si mesma, e apagar uma raiz — a unidade, a pasta do usuário ou os dados do AIB.
    /// </summary>
    public string? Validar(string argumentsJson)
    {
        var (p, recusa) = Ler(argumentsJson);
        if (p == null) return recusa;

        bool ehPasta = Directory.Exists(p.Alvo), ehArquivo = File.Exists(p.Alvo);

        if (p.Acao == "mkdir")
        {
            if (ehArquivo) return $"ERRO: '{p.Alvo}' já existe e é um ARQUIVO, não uma pasta.";
            if (ehPasta) return $"A pasta '{p.Alvo}' já existe; nada a fazer.";
            return null;
        }

        if (!ehPasta && !ehArquivo)
            return PreVooDeCaminho.Conferir($"\"{p.Alvo}\"") ?? $"ERRO: '{p.Alvo}' não existe.";

        if (p.Acao == "delete")
            return Protegido(p.Alvo) is string porque
                ? $"ERRO: '{p.Alvo}' é {porque}, e não é apagado por esta ferramenta."
                : null;

        string destino = p.Destino!;

        if (Directory.Exists(destino) || File.Exists(destino))
            return $"ERRO: '{destino}' já existe. Esta ferramenta nunca sobrescreve: escolha outro "
                   + "destino, ou apague o que está lá primeiro (action=delete).";

        if (string.Equals(destino, p.Alvo, StringComparison.OrdinalIgnoreCase))
            return "ERRO: origem e destino são o mesmo caminho.";

        if (ehPasta && destino.StartsWith(p.Alvo + "\\", StringComparison.OrdinalIgnoreCase))
            return $"ERRO: não dá para pôr a pasta '{p.Alvo}' dentro dela mesma.";

        if (p.Acao is "copy" or "move" && Path.GetDirectoryName(destino) is string pai && !Directory.Exists(pai))
            return $"ERRO: a pasta de destino '{pai}' não existe. Crie antes com action=mkdir.";

        return null;
    }

    /// <summary>Por que um caminho não se apaga daqui, ou <c>null</c>.</summary>
    private static string? Protegido(string alvo)
    {
        string raiz = (Path.GetPathRoot(alvo) ?? "").TrimEnd('\\', '/');
        if (string.Equals(alvo, raiz, StringComparison.OrdinalIgnoreCase)) return "a raiz de uma unidade";

        foreach (var (pasta, nome) in new[]
                 {
                     (Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "a sua pasta de usuário"),
                     (Environment.GetFolderPath(Environment.SpecialFolder.Windows), "a pasta do Windows"),
                     (DirectoryService.DataDir, "a pasta de dados do AIB")
                 })
        {
            if (pasta.Length == 0) continue;
            string p = pasta.TrimEnd('\\', '/');
            if (string.Equals(alvo, p, StringComparison.OrdinalIgnoreCase)) return nome;
            if (nome == "a pasta de dados do AIB" && alvo.StartsWith(p + "\\", StringComparison.OrdinalIgnoreCase))
                return "parte dos dados do AIB (memória, configurações, cofre)";
        }

        return null;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Cartão, piso e dispensa
    // ─────────────────────────────────────────────────────────────────────────

    public CommandConfirmationContext? BuildConfirmationContext(string argumentsJson, int userLevel)
    {
        var (p, _) = Ler(argumentsJson);
        if (p == null) return null;

        string? descricao = Descrever(p);
        if (descricao == null) return null;

        return new CommandConfirmationContext { Tool = Name, Command = descricao, Level = userLevel };
    }

    /// <summary>
    /// A frase que o cartão mostra e que a execução confere de novo. Leva o que só o app sabe
    /// conferir antes: para apagar, copiar e mover uma pasta, quantos arquivos e quanto pesam.
    /// Se isso mudar entre o cartão e a execução, a frase muda, e a execução recusa.
    /// </summary>
    private static string? Descrever(Pedido p)
    {
        bool ehPasta = Directory.Exists(p.Alvo);

        string Medida()
        {
            if (!ehPasta) return File.Exists(p.Alvo) ? $" ({Tamanho(new FileInfo(p.Alvo).Length)})" : "";
            var (arquivos, bytes, parcial) = Contar(p.Alvo);
            string mais = parcial ? "+" : "";
            return arquivos == 0 ? " (vazia)" : $" ({arquivos}{mais} arquivo(s), {Tamanho(bytes)}{mais})";
        }

        string tipo = ehPasta ? "PASTA" : "ARQUIVO";

        return p.Acao switch
        {
            "mkdir" => $"CRIAR PASTA {p.Alvo}",
            "delete" => $"APAGAR {tipo} {p.Alvo}{Medida()}",
            "copy" => $"COPIAR {tipo} {p.Alvo}{Medida()} → {p.Destino}",
            "move" => $"MOVER {tipo} {p.Alvo}{Medida()} → {p.Destino}",
            "rename" => $"RENOMEAR {tipo} {p.Alvo} → {Path.GetFileName(p.Destino)}",
            _ => null
        };
    }

    /// <summary>
    /// A floor list da ferramenta, pela OPERAÇÃO e não por regex sobre texto. Apagar pasta com
    /// conteúdo é a mesma categoria que <c>Remove-Item -Recurse</c> no shell, e exige o mesmo
    /// Nível 7. Sem isto a ferramenta nova seria o caminho por baixo do piso: o shell barrava a
    /// deleção recursiva no nível 2, e esta a faria sem nem passar pela regra.
    /// </summary>
    public string? PisoTipado(CommandConfirmationContext contexto, int userLevel)
    {
        if (userLevel >= 7) return null;

        string c = contexto.Command ?? "";
        bool apagaConteudo = c.StartsWith("APAGAR PASTA ", StringComparison.Ordinal) && !c.EndsWith("(vazia)", StringComparison.Ordinal);

        return apagaConteudo
            ? "ACESSO NEGADO (FLOOR): apagar pasta com conteúdo — requer Nível 7."
            : null;
    }

    /// <summary>
    /// Só CRIAR PASTA é dispensado dentro das pastas sem confirmação. Copiar, mover, renomear e
    /// apagar sempre perguntam: a dispensa foi pensada para gravar e editar, onde o estrago é o
    /// conteúdo de um arquivo; aqui o estrago é a árvore inteira, e o usuário quer ver.
    /// </summary>
    public bool DispensaConfirmacao(string argumentsJson)
    {
        var (p, _) = Ler(argumentsJson);
        return p?.Acao == "mkdir" && PastasSemConfirmacao.Dispensa(p.Alvo);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Execução
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Sem autorização descrita não há o que executar. O registry nunca chama isto.</summary>
    public Task<string> ExecuteAsync(string argumentsJson, int userLevel = 1) =>
        Task.FromResult("ERRO: esta ferramenta só executa o que foi autorizado.");

    /// <summary>
    /// Executa só se o que vai acontecer agora é o que foi aprovado. Entre o cartão e o clique
    /// podem passar horas: a pasta pode ter ganhado arquivos, virado outra coisa, ou sumido.
    /// </summary>
    public Task<string> ExecutarAutorizadoAsync(string argumentsJson, int userLevel, CommandConfirmationContext? autorizado)
    {
        var (p, recusa) = Ler(argumentsJson);
        if (p == null) return Task.FromResult(recusa!);

        if (autorizado == null)
            return Task.FromResult("ERRO: sem autorização descrita, nada foi feito.");

        string? preVoo = Validar(argumentsJson);
        if (preVoo != null) return Task.FromResult(preVoo);

        string? agora = Descrever(p);
        if (!string.Equals(agora, autorizado.Command, StringComparison.Ordinal))
            return Task.FromResult($"ERRO: o alvo mudou desde a autorização (era \"{autorizado.Command}\", "
                                   + $"agora é \"{agora}\"). Nada foi feito; peça de novo.");

        try
        {
            return Task.FromResult(Executar(p, agora!));
        }
        catch (Exception ex)
        {
            return Task.FromResult($"ERRO ao executar '{p.Acao}' em '{p.Alvo}': {ex.Message}");
        }
    }

    private static string Executar(Pedido p, string descricao)
    {
        bool ehPasta = Directory.Exists(p.Alvo);

        switch (p.Acao)
        {
            case "mkdir":
                Directory.CreateDirectory(p.Alvo);
                return $"SUCESSO: pasta '{p.Alvo}' criada.";

            case "delete":
                if (ehPasta)
                    Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(p.Alvo,
                        Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                        Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                else
                    Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(p.Alvo,
                        Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                        Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                return $"SUCESSO: {descricao["APAGAR ".Length..]} foi para a Lixeira — já feito, não repetir sem o usuário pedir.";

            case "copy":
                if (ehPasta) CopiarPasta(p.Alvo, p.Destino!);
                else File.Copy(p.Alvo, p.Destino!, overwrite: false);
                return $"SUCESSO: copiado para '{p.Destino}'.";

            case "move":
            case "rename":
                if (ehPasta) Directory.Move(p.Alvo, p.Destino!);
                else File.Move(p.Alvo, p.Destino!, overwrite: false);
                return $"SUCESSO: '{p.Alvo}' agora é '{p.Destino}'.";
        }

        return $"ERRO: ação '{p.Acao}' desconhecida.";
    }

    private static void CopiarPasta(string origem, string destino)
    {
        Directory.CreateDirectory(destino);
        foreach (string arquivo in Directory.GetFiles(origem))
            File.Copy(arquivo, Path.Combine(destino, Path.GetFileName(arquivo)), overwrite: false);
        foreach (string pasta in Directory.GetDirectories(origem))
            CopiarPasta(pasta, Path.Combine(destino, Path.GetFileName(pasta)));
    }

    /// <summary>Arquivos e bytes de uma pasta, com teto: pasta gigante não trava o cartão.</summary>
    private static (int Arquivos, long Bytes, bool Parcial) Contar(string pasta)
    {
        const int Teto = 50_000;
        var opcoes = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 };

        int n = 0;
        long bytes = 0;
        foreach (var f in new DirectoryInfo(pasta).EnumerateFiles("*", opcoes))
        {
            if (++n > Teto) return (Teto, bytes, true);
            bytes += f.Length;
        }
        return (n, bytes, false);
    }

    private static string Tamanho(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.#} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):0.#} GB"
    };
}
