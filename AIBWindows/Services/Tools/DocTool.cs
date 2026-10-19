using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using AIB.Services.Documentos;
using OpenAI.Chat;

namespace AIB.Services.Tools;

/// <summary>
/// Planilhas (.xlsx .xlsm .xls .csv), Word (.docx) e PDF: ler, procurar, consultar, criar e
/// editar.
/// <para>
/// Pedido do usuário (19/10/2026): uma ferramenta de verdade no lugar da habilidade
/// <c>ler-planilha</c>, que desalinhava colunas quando havia célula vazia, trocava texto com
/// espaço por "System.Xml.XmlElement", lia só a primeira aba e levava 51 s em 20 mil linhas.
/// </para>
/// <para>
/// Ler não pergunta (a não ser dentro da pasta de dados do AIB). Gravar SEMPRE pergunta, sem
/// "sempre permitir", e o cartão mostra o plano — célula por célula, trecho por trecho — que a
/// execução confere antes de gravar (<see cref="PlanoDeGravacao.Impressao"/>). O original
/// ganha uma cópia antes de ser tocado (<see cref="CopiasDeDocumento"/>).
/// </para>
/// <para>
/// As contas são feitas aqui (<c>query</c>), e não pelo modelo: somar 300 linhas lidas como
/// texto é onde ele erra.
/// </para>
/// </summary>
public class DocTool : ITool
{
    private readonly string? _dados;
    private readonly Func<string> _autor;
    private readonly CopiasDeDocumento _copias;

    public static event Action<DocumentoGravado>? Gravou;
    public sealed record DocumentoGravado(string Arquivo, string? Copia, string? Impressao, bool Criado);

    public CopiasDeDocumento Copias => _copias;

    /// <param name="raizDeDados">A pasta de dados do AIB. Nula em produção; os ensaios passam uma temporária.</param>
    /// <param name="autor">
    /// Quem assina as revisões no Word — o nome do personagem ativo. Lido na chamada: o
    /// personagem pode ter sido trocado desde que o app abriu.
    /// </param>
    public DocTool(string? raizDeDados = null, Func<string>? autor = null)
    {
        _dados = raizDeDados;
        _autor = autor ?? (() => "AIB");
        _copias = new CopiasDeDocumento(raizDeDados);
    }

    public string Name => Ferramentas.Documento;

    public string Description =>
        "Planilhas (.xlsx .xlsm .xls .csv), Word (.docx) e PDF. Use no lugar de 'read' e do shell "
        + "para esses formatos. Comece por 'info' (abas, tamanho, cabeçalhos). 'read' traz um trecho "
        + "com a célula ou o ¶ de cada linha; 'find' acha texto; 'query' faz as contas (somar, contar, "
        + "média, agrupar, filtrar): use-a em vez de calcular de cabeça. 'create' e 'edit' gravam "
        + "(.xlsx, .csv, .docx), com cartão de confirmação.";

    public int RequiredLevel => 1;

    public ChatTool ChatToolDefinition => ChatTool.CreateFunctionTool(
        functionName: Name,
        functionDescription: Description,
        functionParameters: BinaryData.FromString("""
        {
            "type": "object",
            "properties": {
                "action": { "type": "string", "enum": ["info", "read", "find", "query", "create", "edit"] },
                "path": { "type": "string", "description": "Caminho absoluto." },
                "sheet": { "type": "string", "description": "Aba (planilha). Sem ela, a primeira." },
                "range": { "type": "string", "description": "read: 'A1:F200' ou '10:60' (linhas); Word: '¶' '12-40'; PDF: páginas '2-4'." },
                "text": { "type": "string", "description": "find: o texto." },
                "query": { "type": "object", "description": "{where:[{col,op:'= != > < >= <= contains',value}], group:col, agg:[{fn:'sum|count|avg|min|max',col}], sort:'-col', limit}. col = cabeçalho ou letra." },
                "ops": { "type": "array", "items": { "type": "object" }, "description": "edit. Planilha: {cell:'B2',value} ('=' inicia fórmula) | {range:'D2:D50',value:'=B{row}*2'} | {insert_rows:N,rows:[[..]]} (antes da linha N) | {delete_rows:'18:19'} | {add_sheet} | {rename_sheet,to}; todas aceitam sheet. Word: {replace:'texto exato',with} | {insert_after:¶,text,style:'Heading1|Heading2|List|Normal'} | {delete:¶} | {table:1,row,col,value}." },
                "content": { "type": "object", "description": "create. Planilha: {sheets:[{name,rows:[[..]]}]}. Word: {blocks:[{style,text} | {list:[..]} | {table:[[..]]}]}." }
            },
            "required": ["action", "path"]
        }
        """)
    );

    // ─────────────────────────────────────────────────────────────────────────
    // Os argumentos
    // ─────────────────────────────────────────────────────────────────────────

    internal sealed record Argumentos(
        string Acao, string Caminho, string? Aba, string? Intervalo, string? Texto,
        JsonElement? Consulta, JsonElement? Ops, JsonElement? Conteudo, bool CaminhoReparado)
    {
        public bool Grava => Acao is "create" or "edit";
    }

    private static readonly string[] Acoes = { "info", "read", "find", "query", "create", "edit" };

    /// <summary>A leitura ÚNICA dos argumentos, usada pelo pré-voo, pelo cartão e pela execução.</summary>
    internal static Argumentos? Ler(string argumentsJson)
    {
        try
        {
            var a = JsonSerializer.Deserialize<JsonElement>(argumentsJson);
            if (a.ValueKind != JsonValueKind.Object) return null;

            string acao = Texto(a, "action")?.Trim().ToLowerInvariant() ?? "";
            string caminho = PathArgumentRepair.Normalize(Texto(a, "path"), out bool reparado);

            return new Argumentos(acao, caminho, Texto(a, "sheet"), Texto(a, "range"), Texto(a, "text"),
                Objeto(a, "query", JsonValueKind.Object), Objeto(a, "ops", JsonValueKind.Array),
                Objeto(a, "content", JsonValueKind.Object), reparado);
        }
        catch (JsonException)
        {
            return null;
        }

        static string? Texto(JsonElement a, string nome) =>
            a.TryGetProperty(nome, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        static JsonElement? Objeto(JsonElement a, string nome, JsonValueKind tipo) =>
            a.TryGetProperty(nome, out var v) && v.ValueKind == tipo ? v.Clone() : null;
    }

    private Acesso AcessoDe(string caminho) => DadosProtegidos.Leitura(caminho, _dados);

    // ─────────────────────────────────────────────────────────────────────────
    // O portão
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Recusa antes do cartão o que não pode dar certo — inclusive uma edição cujo plano não se
    /// monta (célula inválida, trecho que não existe): uma pergunta para algo que vai falhar
    /// ensina a permitir sem ler.
    /// </summary>
    public string? Validar(string argumentsJson)
    {
        var a = Ler(argumentsJson);
        if (a == null) return "ERRO: argumentos ilegíveis. Envie um objeto JSON com 'action' e 'path'.";
        if (!Acoes.Contains(a.Acao)) return $"ERRO: 'action' deve ser uma de: {string.Join(", ", Acoes)}.";
        if (string.IsNullOrWhiteSpace(a.Caminho)) return "ERRO: 'path' é obrigatório (caminho absoluto).";

        string caminho;
        try { caminho = Path.GetFullPath(a.Caminho); }
        catch (Exception ex) { return $"ERRO: caminho inválido '{a.Caminho}': {ex.Message}"; }

        var acesso = AcessoDe(caminho);
        if (acesso == Acesso.Negado) return DadosProtegidos.Recusa(caminho);

        if (a.Grava)
        {
            // A pasta de dados do AIB não é lugar de documento do usuário, e gravar nela mexeria
            // em memória, logs ou skills por um caminho sem as regras deles.
            if (acesso != Acesso.Livre || DadosProtegidos.Leitura(caminho, _dados, seguirAtalho: false) != Acesso.Livre
                || DentroDosDados(caminho))
                return $"ACESSO NEGADO: '{caminho}' fica na pasta de dados do AIB, onde a ferramenta '{Name}' não grava. Nada foi gravado.";

            var formato = a.Acao == "create" && !File.Exists(caminho) ? Formatos.PelaExtensao(caminho) : Formatos.De(caminho);
            if (!Formatos.Gravavel(formato))
                return $"ERRO: '{Path.GetFileName(caminho)}' não se grava por esta ferramenta. Ela grava .xlsx, .xlsm, .csv e .docx; .xls e PDF só se leem.";

            if (a.Acao == "edit" && !File.Exists(caminho))
                return $"ERRO: '{caminho}' não existe. Para um arquivo novo, use action='create'.";
            if (a.Acao == "edit" && (a.Ops is not { } ops || ops.GetArrayLength() == 0))
                return "ERRO: 'edit' precisa de 'ops' com ao menos uma operação.";
            if (a.Acao == "create" && a.Conteudo == null)
                return "ERRO: 'create' precisa de 'content'.";

            // Sem janela para perguntar (ensaio, app sem interface), o arquivo travado é recusado
            // antes do cartão. Com ela, a pergunta vem na hora de gravar (AoTravar).
            if (AoTravar == null && File.Exists(caminho) && Travado(caminho) is { } travado) return travado;

            try { _ = PlanoGuardado(a, caminho); }
            catch (ErroDeDocumento ex) { return "ERRO: " + ex.Message; }
            catch (Exception ex) { return $"ERRO: não consegui preparar a gravação de '{Path.GetFileName(caminho)}': {ex.Message}"; }

            return null;
        }

        if (!File.Exists(caminho))
            return Directory.Exists(caminho)
                ? $"ERRO: '{caminho}' é uma pasta. Use 'glob' para achar o arquivo."
                : $"ERRO: '{caminho}' não existe. Nada foi lido.";

        if (Formatos.De(caminho) == Formato.Desconhecido)
            return $"ERRO: '{Path.GetFileName(caminho)}' não é planilha, Word nem PDF que esta ferramenta leia. Para texto, use '{Ferramentas.Ler}'.";

        if (a.Acao == "find" && string.IsNullOrWhiteSpace(a.Texto)) return "ERRO: 'find' precisa de 'text'.";
        if (a.Acao == "query" && a.Consulta == null) return "ERRO: 'query' precisa do objeto 'query'.";

        return null;
    }

    /// <summary>Ler não pergunta (fora da pasta de dados do AIB); gravar sempre pergunta.</summary>
    public bool PedeConfirmacao(string argumentsJson)
    {
        var a = Ler(argumentsJson);
        if (a == null) return true;
        if (a.Grava) return true;
        return AcessoDe(Resolver(a.Caminho)) == Acesso.Pergunta;
    }

    public bool PedeFalaDoUsuario(string argumentsJson) => Ler(argumentsJson)?.Grava ?? true;

    /// <summary>Gravar documento fica a partir do Nível 2, como o <c>write</c> e o <c>edit</c>.</summary>
    public string? PisoTipado(CommandConfirmationContext contexto, int userLevel) =>
        contexto.Plano != null && userLevel < 2
            ? "gravar documentos exige o Nível 2, como gravar e editar arquivos"
            : null;

    public CommandConfirmationContext? BuildConfirmationContext(string argumentsJson, int userLevel)
    {
        var a = Ler(argumentsJson);
        if (a == null) return null;
        string caminho = Resolver(a.Caminho);

        if (!a.Grava) return DadosProtegidos.Cartao(Name, "LER", caminho, userLevel);

        PlanoDeGravacao plano;
        try { plano = PlanoGuardado(a, caminho).Plano; }
        catch { return null; }

        return new CommandConfirmationContext
        {
            Tool = Name,
            Command = (plano.Criar ? "CRIAR " : "EDITAR ") + caminho,
            Level = userLevel,
            Cwd = Environment.CurrentDirectory,
            ScriptBody = plano.EmTexto(),
            Plano = plano,
            SemSempre = true
        };
    }

    // ─────────────────────────────────────────────────────────────────────────
    // A execução
    // ─────────────────────────────────────────────────────────────────────────

    public Task<string> ExecuteAsync(string argumentsJson, int userLevel = 1) =>
        ExecutarAutorizadoAsync(argumentsJson, userLevel, null);

    public Task<string> ExecutarAutorizadoAsync(string argumentsJson, int userLevel, CommandConfirmationContext? autorizado) =>
        Task.Run(() => ExecutarAsync(argumentsJson, autorizado));

    private async Task<string> ExecutarAsync(string argumentsJson, CommandConfirmationContext? autorizado)
    {
        var a = Ler(argumentsJson);
        if (a == null) return "ERRO: argumentos ilegíveis.";
        string caminho = Resolver(a.Caminho);

        if (Validar(argumentsJson) is { } recusa) return recusa;

        try
        {
            if (a.Grava) return await GravarAsync(a, caminho, autorizado);

            if (AcessoDe(caminho) == Acesso.Pergunta && autorizado == null)
                return DadosProtegidos.SemCartao(caminho);

            int teto = TetoDeSaida;
            var formato = Formatos.De(caminho);

            if (Formatos.Planilha(formato))
            {
                var pasta = LeitorDePlanilha.Abrir(caminho);
                return a.Acao switch
                {
                    "info" => Apresentacao.Info(pasta, caminho),
                    "read" => Apresentacao.Ler(pasta, a.Aba, a.Intervalo, teto),
                    "find" => Apresentacao.Buscar(pasta, a.Texto!, teto),
                    "query" => ConsultaDePlanilha.Executar(pasta, a.Aba, a.Consulta!.Value, teto),
                    _ => "ERRO: ação desconhecida."
                };
            }

            var doc = LeitorDeDocumento.Abrir(caminho);
            return a.Acao switch
            {
                "info" => Apresentacao.Info(doc, caminho),
                "read" => Apresentacao.Ler(doc, a.Intervalo, teto),
                "find" => Apresentacao.Buscar(doc, a.Texto!, teto),
                "query" => "ERRO: 'query' é para planilhas. Em Word e PDF, use 'find' e 'read'.",
                _ => "ERRO: ação desconhecida."
            };
        }
        catch (ErroDeDocumento ex)
        {
            return "ERRO: " + ex.Message;
        }
        catch (IOException ex) when (EhTrava(ex))
        {
            return MensagemDeTrava(caminho);
        }
        catch (Exception ex)
        {
            return $"ERRO ao abrir '{Path.GetFileName(caminho)}': {ex.Message}";
        }
    }

    private async Task<string> GravarAsync(Argumentos a, string caminho, CommandConfirmationContext? autorizado)
    {
        // Gravar sem cartão não acontece: o registry sempre pergunta (PedeConfirmacao), e quem
        // chegar aqui sem ele é recusado.
        if (autorizado?.Plano is not { } pedido)
            return $"ACESSO NEGADO: gravar '{caminho}' exige a confirmação do usuário. Nada foi gravado.";

        var gravacao = Planejar(a, caminho);

        // O usuário autorizou ESTE antes e depois. Se o arquivo mudou entre a pergunta e o
        // "Permitir" (alguém salvou no Excel), o plano de agora é outro, e gravar seria fazer
        // algo que ninguém viu.
        if (gravacao.Plano.Impressao != pedido.Impressao || gravacao.Plano.EmTexto() != pedido.EmTexto())
            return $"ERRO: '{Path.GetFileName(caminho)}' mudou depois da pergunta. Nada foi gravado; peça de novo para ver o que muda agora.";

        // Aberto no Excel ou no Word: a janela de escolha pergunta o que fazer (pedido do
        // usuário, 19/10/2026) — fechar e tentar de novo, salvar como cópia, ou desistir.
        string? destino = null;
        while (File.Exists(caminho) && Travado(caminho) is { } travado)
        {
            if (AoTravar == null) return travado;

            var escolha = await AoTravar(caminho);
            if (escolha == NaTrava.Desistir)
                return $"ERRO: '{Path.GetFileName(caminho)}' está aberto em outro programa, e o usuário desistiu de gravar. Nada foi gravado.";
            if (escolha == NaTrava.SalvarCopia)
            {
                destino = CaminhoDaCopia(caminho);
                break;
            }
        }

        string? copia = null;
        if (destino == null && (!gravacao.Plano.Criar || File.Exists(caminho)))
            copia = _copias.Guardar(caminho);

        string alvo = destino ?? caminho;
        try
        {
            gravacao.Gravar(destino);
        }
        catch (IOException ex) when (EhTrava(ex))
        {
            return MensagemDeTrava(alvo);
        }

        string? impressaoDepois = Gravacao.Impressao(alvo);
        try
        {
            Gravou?.Invoke(new DocumentoGravado(alvo, copia, impressaoDepois, gravacao.Plano.Criar || destino != null));
        }
        catch { }

        if (destino != null)
            return $"SUCESSO: {gravacao.Resultado}, salvo COMO CÓPIA em '{destino}': '{Path.GetFileName(caminho)}' estava aberto "
                   + "em outro programa e ficou como estava.";

        string onde = copia == null ? "" : $" O original ficou guardado em '{copia}'.";
        string aviso = a.CaminhoReparado
            ? " AVISO: o caminho tinha escapes JSON inválidos e foi corrigido. Escreva a barra invertida duplicada."
            : "";
        return $"SUCESSO: {gravacao.Resultado} em '{caminho}'.{onde}{aviso}";
    }

    private readonly object _trava = new();
    private (string Chave, Gravacao Gravacao)? _guardado;

    /// <summary>
    /// O plano do pré-voo e do cartão, refeito só se o pedido ou o arquivo mudaram. Montar o
    /// plano de uma planilha de 20 mil linhas leva segundos, e o registry pede o pré-voo e o
    /// cartão em seguida. A GRAVAÇÃO não usa isto: ela refaz o plano do zero e compara.
    /// </summary>
    private Gravacao PlanoGuardado(Argumentos a, string caminho)
    {
        var info = new FileInfo(caminho);
        string chave = JsonSerializer.Serialize(a) + "|" + caminho + "|"
                       + (info.Exists ? $"{info.LastWriteTimeUtc.Ticks}:{info.Length}" : "novo");

        lock (_trava)
        {
            if (_guardado is { } g && g.Chave == chave) return g.Gravacao;
        }

        var nova = Planejar(a, caminho);
        lock (_trava) _guardado = (chave, nova);
        return nova;
    }

    /// <summary>O plano da gravação, pelo formato.</summary>
    internal Gravacao Planejar(Argumentos a, string caminho)
    {
        bool existe = File.Exists(caminho);
        var formato = existe ? Formatos.De(caminho) : Formatos.PelaExtensao(caminho);

        return (a.Acao, formato) switch
        {
            ("create", Formato.Docx) => EditorDeDocx.PlanejarCriacao(caminho, a.Conteudo!.Value),
            ("edit", Formato.Docx) => EditorDeDocx.Planejar(caminho, a.Ops!.Value, Autor()),
            ("create", _) => EditorDePlanilha.PlanejarCriacao(caminho, a.Conteudo!.Value),
            _ => EditorDePlanilha.Planejar(caminho, a.Ops!.Value, a.Aba)
        };
    }

    private string Autor()
    {
        string nome = _autor()?.Trim() ?? "";
        return nome.Length == 0 ? "AIB" : nome;
    }

    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Quanto texto uma resposta leva, no máximo. Planilha grande se navega por trechos.</summary>
    internal static int TetoDeSaida => Math.Max(8000, LimitesDoProvedor.Atual.LinhasDeLeitura * 40);

    private static string Resolver(string caminho)
    {
        try { return Path.GetFullPath(caminho); }
        catch { return caminho; }
    }

    private bool DentroDosDados(string caminho)
    {
        string raiz = Path.GetFullPath(_dados ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".AIB"));
        string c = Path.GetFullPath(caminho);
        return c.StartsWith(raiz.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>O que fazer com o arquivo travado por outro programa.</summary>
    public enum NaTrava { TentarDeNovo, SalvarCopia, Desistir }

    /// <summary>
    /// A janela que pergunta o que fazer com o arquivo travado. Ligada pelo App (é interface);
    /// nula nos ensaios e sem interface — aí o travado é recusado antes do cartão.
    /// </summary>
    public static Func<string, Task<NaTrava>>? AoTravar { get; set; }

    /// <summary>"Custos.xlsx" → "Custos (AIB).xlsx"; se existir, "Custos (AIB 2).xlsx"...</summary>
    internal static string CaminhoDaCopia(string caminho)
    {
        string pasta = Path.GetDirectoryName(caminho) ?? ".";
        string nome = Path.GetFileNameWithoutExtension(caminho), ext = Path.GetExtension(caminho);
        for (int n = 1; ; n++)
        {
            string c = Path.Combine(pasta, n == 1 ? $"{nome} (AIB){ext}" : $"{nome} (AIB {n}){ext}");
            if (!File.Exists(c)) return c;
        }
    }

    /// <summary>
    /// O Excel e o Word travam o arquivo aberto. Saber disso ANTES do cartão poupa uma
    /// autorização para uma gravação que ia falhar.
    /// </summary>
    internal static string? Travado(string caminho)
    {
        try
        {
            using var fs = new FileStream(caminho, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
            return null;
        }
        catch (IOException ex) when (EhTrava(ex))
        {
            return MensagemDeTrava(caminho);
        }
        catch (UnauthorizedAccessException)
        {
            return $"ERRO: sem permissão para gravar '{caminho}' (somente leitura?). Nada foi gravado.";
        }
    }

    private static bool EhTrava(IOException ex) =>
        (ex.HResult & 0xFFFF) is 32 or 33; // ERROR_SHARING_VIOLATION, ERROR_LOCK_VIOLATION

    private static string MensagemDeTrava(string caminho) =>
        $"ERRO: '{Path.GetFileName(caminho)}' está aberto em outro programa (Excel ou Word?), que trava o arquivo. "
        + "Nada foi gravado. Peça ao usuário para fechá-lo e tente de novo.";
}
