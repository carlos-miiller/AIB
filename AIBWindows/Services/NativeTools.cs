using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using OpenAI.Chat;
using UglyToad.PdfPig;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using DocumentFormat.OpenXml.Spreadsheet;
using System.Text;
using System.Runtime.InteropServices;
using AIB.Views;

namespace AIB.Services;

internal static class ToolArgParser
{
    internal static string Get(string json, string key)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty(key, out var val))
                return val.ToString() ?? string.Empty;
        }
        catch { }

        try
        {
            int start = json.IndexOf('{');
            int end = json.LastIndexOf('}');
            if (start >= 0 && end > start)
            {
                string cleaned = json[start..(end + 1)];
                using var doc = JsonDocument.Parse(cleaned);
                if (doc.RootElement.TryGetProperty(key, out var val))
                    return val.GetString() ?? string.Empty;
            }
        }
        catch { }

        try
        {
            string pattern = $"\"{key}\"";
            int keyIdx = json.IndexOf(pattern, StringComparison.OrdinalIgnoreCase);
            if (keyIdx >= 0)
            {
                int colonIdx = json.IndexOf(':', keyIdx + pattern.Length);
                if (colonIdx >= 0)
                {
                    int quoteStart = json.IndexOf('"', colonIdx + 1);
                    if (quoteStart >= 0)
                    {
                        int quoteEnd = json.IndexOf('"', quoteStart + 1);
                        if (quoteEnd > quoteStart)
                            return json[(quoteStart + 1)..quoteEnd];
                    }
                }
            }
        }
        catch { }

        return string.Empty;
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// FERRAMENTA: manage_memory — Guarda e recupera fatos
// ─────────────────────────────────────────────────────────────────────────────

public class ManageMemoryTool : ITool
{
    public string Name => "manage_memory";
    public string Description => "Gerencia a memória de longo prazo. Pode salvar ('remember') ou buscar ('recall') informações.";
    public int RequiredLevel => 1;

    public ChatTool ChatToolDefinition => ChatTool.CreateFunctionTool(
        Name, Description,
        BinaryData.FromString("""
        {
          "type": "object",
          "properties": {
            "action": { "type": "string", "description": "'remember' para salvar, 'recall' para buscar." },
            "key": { "type": "string", "description": "Para remember: Identificador do fato. Para recall: Palavra-chave da busca." },
            "info": { "type": "string", "description": "Para remember: O conteúdo a ser memorizado." }
          },
          "required": ["action", "key"]
        }
        """));

    public async Task<string> ExecuteAsync(string argumentsJson, int userLevel = 1)
    {
        string action = ToolArgParser.Get(argumentsJson, "action");
        string key = ToolArgParser.Get(argumentsJson, "key");
        string info = ToolArgParser.Get(argumentsJson, "info");

        if (action == "remember")
        {
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(info)) return "ERRO: 'key' e 'info' são obrigatórios para remember.";
            return await MemoryService.RememberAsync(key, info);
        }
        else if (action == "recall")
        {
            if (string.IsNullOrWhiteSpace(key)) return "ERRO: 'key' (query) é obrigatório para recall.";
            return MemoryService.Recall(key);
        }
        return "ERRO: 'action' deve ser 'remember' ou 'recall'.";
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// FERRAMENTA: manage_vault — Guarda ou recupera credenciais
// ─────────────────────────────────────────────────────────────────────────────

public class ManageVaultTool : ITool
{
    public string Name => "manage_vault";
    public string Description => "Guarda ('store') ou recupera ('retrieve') credenciais do cofre nativo criptografado.";
    public int RequiredLevel => 7;

    public ChatTool ChatToolDefinition => ChatTool.CreateFunctionTool(
        Name, Description,
        BinaryData.FromString("""
        {
          "type": "object",
          "properties": {
            "action": { "type": "string", "description": "'store' ou 'retrieve'." },
            "system": { "type": "string", "description": "Nome do sistema/serviço." },
            "key": { "type": "string", "description": "Nome da chave." },
            "value": { "type": "string", "description": "Para store: O valor secreto." }
          },
          "required": ["action", "system", "key"]
        }
        """));

    public async Task<string> ExecuteAsync(string argumentsJson, int userLevel = 1)
    {
        string action = ToolArgParser.Get(argumentsJson, "action");
        string system = ToolArgParser.Get(argumentsJson, "system");
        string key = ToolArgParser.Get(argumentsJson, "key");
        string value = ToolArgParser.Get(argumentsJson, "value");

        if (string.IsNullOrWhiteSpace(system) || string.IsNullOrWhiteSpace(key))
            return "ERRO: 'system' e 'key' são obrigatórios.";

        if (action == "store")
        {
            if (string.IsNullOrWhiteSpace(value)) return "ERRO: 'value' obrigatório para store.";
            return await CredentialService.StoreCredentialAsync(system, key, value);
        }
        else if (action == "retrieve")
        {
            return CredentialService.RetrieveCredential(system, key);
        }
        return "ERRO: 'action' deve ser 'store' ou 'retrieve'.";
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// FERRAMENTA: read_file — Lê arquivos
// ─────────────────────────────────────────────────────────────────────────────

public class ReadFileTool : ITool
{
    public string Name => "read_file";
    public string Description => "Lê o conteúdo de um arquivo do sistema (texto, .pdf, .docx, .xlsx). Aceita apenas caminhos absolutos.";
    public int RequiredLevel => 1;

    public ChatTool ChatToolDefinition => ChatTool.CreateFunctionTool(
        Name, Description,
        BinaryData.FromString("""
        {
          "type": "object",
          "properties": {
            "path": { "type": "string", "description": "Caminho absoluto do arquivo a ser lido (ex: 'C:\\Docs\\relatorio.pdf')." }
          },
          "required": ["path"]
        }
        """));

    public async Task<string> ExecuteAsync(string argumentsJson, int userLevel = 1)
    {
        string path = ToolArgParser.Get(argumentsJson, "path");
        if (string.IsNullOrWhiteSpace(path)) return "ERRO: 'path' é obrigatório.";
        
        path = path.Trim('\"', '\''); // Limpeza
        if (!File.Exists(path)) return $"ERRO: Arquivo não encontrado no caminho fornecido: {path}";

        string ext = Path.GetExtension(path).ToLowerInvariant();
        try
        {
            return await Task.Run(() =>
            {
                if (ext == ".pdf") return ReadPdf(path);
                if (ext == ".docx") return ReadWord(path);
                if (ext == ".xlsx") return ReadExcel(path);
                
                return File.ReadAllText(path);
            });
        }
        catch (Exception ex)
        {
            return $"ERRO ao ler {ext}: {ex.Message}";
        }
    }

    private string ReadPdf(string path)
    {
        var sb = new StringBuilder();
        using (PdfDocument document = PdfDocument.Open(path))
        {
            foreach (var page in document.GetPages())
            {
                sb.AppendLine(page.Text);
            }
        }
        return sb.ToString();
    }

    private string ReadWord(string path)
    {
        var sb = new StringBuilder();
        using (WordprocessingDocument wordDoc = WordprocessingDocument.Open(path, false))
        {
            var body = wordDoc.MainDocumentPart?.Document?.Body;
            if (body == null) return string.Empty;
            
            foreach (var paragraph in body.Elements<Paragraph>())
            {
                sb.AppendLine(paragraph.InnerText);
            }
        }
        return sb.ToString();
    }

    private string ReadExcel(string path)
    {
        var sb = new StringBuilder();
        using (SpreadsheetDocument spreadsheetDoc = SpreadsheetDocument.Open(path, false))
        {
            var workbookPart = spreadsheetDoc.WorkbookPart;
            if (workbookPart == null) return string.Empty;
            
            var sstPart = workbookPart.GetPartsOfType<SharedStringTablePart>().FirstOrDefault();
            var sharedStringTable = sstPart?.SharedStringTable;

            foreach (var worksheetPart in workbookPart.WorksheetParts)
            {
                var sheetData = worksheetPart.Worksheet?.Elements<SheetData>().FirstOrDefault();
                if (sheetData == null) continue;

                foreach (var row in sheetData.Elements<Row>())
                {
                    var rowData = new System.Collections.Generic.List<string>();
                    foreach (var cell in row.Elements<Cell>())
                    {
                        string cellValue = cell.CellValue?.Text ?? string.Empty;
                        
                        if (cell.DataType != null && cell.DataType.Value == CellValues.SharedString)
                        {
                            if (int.TryParse(cellValue, out int id) && sharedStringTable != null)
                            {
                                cellValue = sharedStringTable.ElementAt(id).InnerText;
                            }
                        }
                        rowData.Add(cellValue);
                    }
                    sb.AppendLine(string.Join(" | ", rowData));
                }
                sb.AppendLine("---");
            }
        }
        return sb.ToString();
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// FERRAMENTA: run_command — Executa comandos de terminal
// ─────────────────────────────────────────────────────────────────────────────

public class RunCommandTool : ITool
{
    // D-08 (Phase 3): o semáforo de modal foi promovido para
    // <see cref="CommandConfirmationWindow"/> como _modalLock estático, compartilhado
    // por RunCommandTool + ExecuteSkillTool + MaterializeSkillTool via ShowAsync.
    // Mantemos a tool magrinha — sem campo local.

    public string Name => "run_command";
    public string Description => "Executa comandos no shell do usuário (cmd.exe /c) e retorna a saída. Para invocar cmdlets PowerShell, prefixe com 'powershell -NoProfile -Command \"...\"'. Para caminhos com espaço, use aspas.";
    public int RequiredLevel => 2;

    public ChatTool ChatToolDefinition => ChatTool.CreateFunctionTool(
        Name, Description,
        BinaryData.FromString("""
        {
          "type": "object",
          "properties": {
            "command": {
              "type": "string",
              "description": "O comando completo a executar (ex: 'dir C:\\Users', 'python --version')."
            }
          },
          "required": ["command"]
        }
        """));

    public async Task<string> ExecuteAsync(string argumentsJson, int userLevel = 1)
    {
        // [1] Parse e validação do payload.
        string command = ToolArgParser.Get(argumentsJson, "command");
        if (string.IsNullOrWhiteSpace(command)) return "ERRO: 'command' é obrigatório.";

        // [2] CWD que cmd.exe vai usar; este é o mesmo valor que aparece no modal (D3).
        string cwd = DirectoryService.DataDir;

        // D-04 (Phase 3): pré-computa o veredito do floor list para que o modal
        // renderize o banner AVISO antes do clique. Em userLevel >= 7 o helper
        // retorna (false, null) por design (D-01: floor inativo).
        var (floorHit, floorReason) = CommandFloorList.Match(command, userLevel);

        // [3] Contexto imutável passado ao modal.
        var ctx = new CommandConfirmationContext
        {
            Tool = "run_command",
            Command = command,
            Level = userLevel,
            Cwd = cwd,
            DenylistHit = floorHit,
            DenylistReason = floorReason,
        };

        // [4] AlwaysAllow fast path (D2 + D-10): a chave agora é a tupla
        // (Tool, Cmd, ContentHash?) — para run_command o ContentHash é null.
        // O cast (string?)null é obrigatório para o compilador escolher a
        // sobrecarga ValueTuple correta (null literal sozinho é ambíguo).
        var allowKey = (ctx.Tool, ctx.Command, (string?)null);
        if (AlwaysAllowSession.Contains(allowKey))
        {
            _ = AuditLogService.AppendAsync(BuildEntry(ctx, "always_allow", true));
        }
        else
        {
            // [5] No-UI guard (D1): se não há WPF host vivo, recuse — nunca
            // implicitamente permita quando o humano não pode aprovar. O guard
            // permanece local à tool (em vez de delegar ao ShowAsync) para
            // emitir o outcome "deny_no_ui" com o ctx completo no audit log.
            if (System.Windows.Application.Current == null)
            {
                _ = AuditLogService.AppendAsync(BuildEntry(ctx, "deny_no_ui", false));
                return "ACESSO NEGADO: interface de confirmação indisponível.";
            }

            // [6] Modal hop (D-08): ShowAsync carrega o _modalLock estático e
            // o Dispatcher.InvokeAsync compartilhados com as skill tools.
            var (allowed, alwaysAllow) = await CommandConfirmationWindow.ShowAsync(ctx);

            if (!allowed)
            {
                _ = AuditLogService.AppendAsync(BuildEntry(ctx, "deny", false));
                return "Comando recusado pelo usuário";
            }

            if (alwaysAllow)
            {
                AlwaysAllowSession.Add(allowKey);
                _ = AuditLogService.AppendAsync(BuildEntry(ctx, "always_allow", true));
            }
            else
            {
                _ = AuditLogService.AppendAsync(BuildEntry(ctx, "allow", false));
            }
        }

        // [7] Floor gate (D-04, herda Phase 1 D5 + D8):
        // CommandFloorList.Match já retornou (false, null) em userLevel >= 7 (D-01),
        // então floorHit só é true em userLevel < 7. ConfirmDangerousCommands continua
        // gateando se o floor roda (Phase 1 D8 carve-out preservado). Quando o usuário
        // já clicou Permitir mas o floor refuta, registramos o novo outcome
        // "allow_then_floor_deny" para distinguir do "deny" pré-modal.
        var settings = new SettingsService().LoadSettings();
        if (floorHit && settings.ConfirmDangerousCommands)
        {
            _ = AuditLogService.AppendAsync(BuildEntry(ctx, "allow_then_floor_deny", false));
            return floorReason!;
        }

        // [8] Execução real do comando — depois do modal e (opcionalmente) do floor.
        return await CommandService.ExecuteAsync(command, cwd);
    }

    // D-10 (Phase 3): BuildEntry agora delega para o helper compartilhado em
    // ModalAuditEntry para que RunCommandTool, ExecuteSkillTool e
    // MaterializeSkillTool emitam linhas JSONL idênticas no audit.
    private static object BuildEntry(CommandConfirmationContext ctx, string outcome, bool alwaysAllow)
        => ModalAuditEntry.Build(ctx, outcome, alwaysAllow);
}

// ─────────────────────────────────────────────────────────────────────────────
// HELPER COMPARTILHADO: schema do audit log (D-10)
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Constrói a entrada anônima que <see cref="AuditLogService"/> serializa como
/// linha JSONL. Compartilhado pelos três modal-bearing tools (run_command,
/// execute_skill, materialize_skill) — content_hash é null para run_command
/// e o hex SHA256 do corpo do script para as skill tools (D-10).
/// </summary>
internal static class ModalAuditEntry
{
    public static object Build(CommandConfirmationContext ctx, string outcome, bool alwaysAllow) => new
    {
        ts = DateTime.UtcNow.ToString("o"),
        tool = ctx.Tool,
        cmd = ctx.Command,
        level = ctx.Level,
        cwd = ctx.Cwd,
        outcome,
        always_allow = alwaysAllow,
        content_hash = ctx.ContentHash,
    };
}

// ─────────────────────────────────────────────────────────────────────────────
// FERRAMENTA: search_web — Pesquisa web
// ─────────────────────────────────────────────────────────────────────────────

public class SearchWebTool : ITool
{
    public string Name => "search_web";
    public string Description => "Pesquisa na internet em tempo real via DuckDuckGo.";
    public int RequiredLevel => 3;

    public ChatTool ChatToolDefinition => ChatTool.CreateFunctionTool(
        Name, Description,
        BinaryData.FromString("""
        {
          "type": "object",
          "properties": {
            "query": { "type": "string", "description": "O termo a pesquisar na internet." }
          },
          "required": ["query"]
        }
        """));

    public async Task<string> ExecuteAsync(string argumentsJson, int userLevel = 1)
    {
        string query = ToolArgParser.Get(argumentsJson, "query");
        if (string.IsNullOrWhiteSpace(query)) return "ERRO: 'query' é obrigatório.";
        return await WebSearchService.SearchAsync(query);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// FERRAMENTA: read_screen — Lê tudo que está na tela (Nome da Janela e OCR)
// ─────────────────────────────────────────────────────────────────────────────

public class ReadScreenTool : ITool
{
    public string Name => "read_screen";
    public string Description => "Captura o título da janela ativa E o texto da tela onde o usuário está focado (OCR). Lê apenas a tela com o cursor para reduzir ruído de payload.";
    public int RequiredLevel => 3;

    public ChatTool ChatToolDefinition => ChatTool.CreateFunctionTool(
        Name, Description,
        BinaryData.FromString("""{ "type": "object", "properties": {} }"""));

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);

    public async Task<string> ExecuteAsync(string argumentsJson, int userLevel = 1)
    {
        try
        {
            string windowTitle = "[NENHUMA JANELA ATIVA]";
            IntPtr handle = GetForegroundWindow();
            if (handle != IntPtr.Zero)
            {
                var sb = new StringBuilder(256);
                if (GetWindowText(handle, sb, 256) > 0)
                {
                    windowTitle = sb.ToString();
                }
            }

            var ocr = new OcrService();
            // Apenas tela ativa: reduz drasticamente o payload de OCR (problema dos 7k+ tokens
            // em multi-monitor). Para varrer todas as telas, use `read_all_screens`.
            string text = await ocr.ExtractTextFromActiveScreenAsync();
            string context = string.IsNullOrWhiteSpace(text)
                ? "[Nenhum texto detectado via OCR]"
                : text;

            return $"JANELA ATIVA: {windowTitle}\n\n[CONTEXTO VISUAL DA TELA - OCR]:\n{context}";
        }
        catch (Exception ex)
        {
            return $"ERRO ao capturar tela: {ex.Message}";
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// FERRAMENTA: execute_skill — Executa skill dinâmica local
// ─────────────────────────────────────────────────────────────────────────────

public class ExecuteSkillTool : ITool
{
    public string Name => "execute_skill";
    public string Description => "Executa uma habilidade dinâmica local (script python/powershell).";
    // D-09 (Phase 3): elevated from 1 to 6. Skill execution agora exige aprovação humana
    // explícita via modal com preview do corpo do script (defesa anti-prompt-injection +
    // anti-silent-edit). Markdown-only skills (instruções, não código executável) ainda
    // passam pelo gate de nível, mas ignoram o modal abaixo.
    public int RequiredLevel => 6;

    public ChatTool ChatToolDefinition => ChatTool.CreateFunctionTool(
        Name, Description,
        BinaryData.FromString("""
        {
          "type": "object",
          "properties": {
            "skill_name": { "type": "string", "description": "Nome da skill." },
            "arguments": { "type": "string", "description": "String de argumentos para a linha de comando do script." }
          },
          "required": ["skill_name"]
        }
        """));

    public async Task<string> ExecuteAsync(string argumentsJson, int userLevel = 1)
    {
        string skillName = ToolArgParser.Get(argumentsJson, "skill_name");
        string args = ToolArgParser.Get(argumentsJson, "arguments");
        if (string.IsNullOrWhiteSpace(skillName)) return "ERRO: 'skill_name' é obrigatório.";

        // [1] Skill lookup (precisa rodar antes do modal — sem skill, sem ctx).
        var skill = SkillService.ListLocalSkills().FirstOrDefault(s => s.Name.Equals(skillName, StringComparison.OrdinalIgnoreCase));
        if (skill == null) return $"Erro: Skill '{skillName}' não encontrada.";

        // [2] D-09 markdown bypass: SKILL.md são instruções (texto humano), nunca
        // executam — pulam o modal e voltam direto. O gate de nível já filtrou
        // chamadas L<6 pelo ToolRegistry.
        if (skill.Interpreter == "markdown")
            return await SkillService.RunSkillAsync(skillName, args);

        // [3] D-09: lê o corpo do script + SHA256 das *bytes inteiras* (não da preview).
        // Edits silenciosos passados do cap de 50KB ainda invalidam a tupla AlwaysAllow.
        var (body, hash) = ReadAndHashSkillFile(skill.ScriptFile);

        // [4] Build context — DenylistHit/DenylistReason ficam default (false/null):
        // skills NÃO consultam o floor list porque o corpo do script é visível ao usuário
        // via preview, e a execução vai por ExecuteWithArgListAsync (sem cmd.exe na cadeia).
        var ctx = new CommandConfirmationContext
        {
            Tool = "execute_skill",
            Command = $"{skill.Interpreter} {skill.Name} {args}".TrimEnd(),
            Level = userLevel,
            Cwd = Path.GetDirectoryName(skill.ScriptFile) ?? "",
            ScriptBody = body,
            Interpreter = skill.Interpreter,
            ContentHash = hash,
        };

        // [5] D-10 tuple AlwaysAllow key — ContentHash é o hex SHA256.
        var allowKey = (ctx.Tool, ctx.Command, ctx.ContentHash);

        if (AlwaysAllowSession.Contains(allowKey))
        {
            _ = AuditLogService.AppendAsync(ModalAuditEntry.Build(ctx, "always_allow", true));
        }
        else
        {
            // [6] No-UI guard espelha RunCommandTool — emite audit com ctx completo.
            if (System.Windows.Application.Current == null)
            {
                _ = AuditLogService.AppendAsync(ModalAuditEntry.Build(ctx, "deny_no_ui", false));
                return "ACESSO NEGADO: interface de confirmação indisponível.";
            }

            // [7] D-08 modal hop (semáforo compartilhado dentro do ShowAsync).
            var (allowed, alwaysAllow) = await CommandConfirmationWindow.ShowAsync(ctx);

            if (!allowed)
            {
                _ = AuditLogService.AppendAsync(ModalAuditEntry.Build(ctx, "deny", false));
                return "Comando recusado pelo usuário";
            }

            if (alwaysAllow)
            {
                AlwaysAllowSession.Add(allowKey);
                _ = AuditLogService.AppendAsync(ModalAuditEntry.Build(ctx, "always_allow", true));
            }
            else
            {
                _ = AuditLogService.AppendAsync(ModalAuditEntry.Build(ctx, "allow", false));
            }
        }

        // [8] Execução real — Plan 01 já roteia via ExecuteWithArgListAsync.
        return await SkillService.RunSkillAsync(skillName, args);
    }

    /// <summary>
    /// D-09: lê o script do disco, hasheia as *bytes inteiras* com SHA256, e devolve
    /// um corpo cap-50KB com marcador de truncamento. O hash é sobre o arquivo todo
    /// para que edits silenciosos passados do cap ainda invalidem o AlwaysAllow.
    /// Retorna (null, null) se o arquivo sumiu entre o lookup e a leitura.
    /// </summary>
    private static (string? Body, string? Hash) ReadAndHashSkillFile(string scriptPath)
    {
        if (!File.Exists(scriptPath)) return (null, null);
        var bytes = File.ReadAllBytes(scriptPath);
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        var body = Encoding.UTF8.GetString(bytes);
        const int Cap = 50_000;
        if (body.Length > Cap) body = body.Substring(0, Cap) + "\n\n[... SCRIPT TRUNCATED]";
        return (body, hash);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// FERRAMENTA: materialize_skill — Cria script de skill
// ─────────────────────────────────────────────────────────────────────────────

public class MaterializeSkillTool : ITool
{
    public string Name => "materialize_skill";
    public string Description => "Cria ou atualiza uma skill local (script).";
    // D-09 (Phase 3): elevated from 5 to 8. Escrever script no disco a partir de
    // conteúdo fornecido pelo LLM é um vetor de tampering — exige aprovação humana
    // explícita via modal com preview do corpo (D-08).
    public int RequiredLevel => 8;

    public ChatTool ChatToolDefinition => ChatTool.CreateFunctionTool(
        Name, Description,
        BinaryData.FromString("""
        {
          "type": "object",
          "properties": {
            "skill_name":     { "type": "string", "description": "Nome único da skill." },
            "script_content": { "type": "string", "description": "Conteúdo completo do script." },
            "interpreter":    { "type": "string", "description": "'python' ou 'powershell'.", "enum": ["python", "powershell"] }
          },
          "required": ["skill_name", "script_content", "interpreter"]
        }
        """));

    public async Task<string> ExecuteAsync(string argumentsJson, int userLevel = 1)
    {
        string name = ToolArgParser.Get(argumentsJson, "skill_name");
        string content = ToolArgParser.Get(argumentsJson, "script_content");
        string interp = ToolArgParser.Get(argumentsJson, "interpreter");
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(content))
            return "ERRO: 'skill_name' e 'script_content' são obrigatórios.";
        if (string.IsNullOrWhiteSpace(interp)) interp = "powershell";

        // [1] Compute destination path (surface no modal via ctx.Command — RESEARCH Open Q#1).
        string ext = interp == "python" ? "py" : "ps1";
        string destPath = Path.Combine(DirectoryService.DataDir, "skills", name, $"{name}.{ext}");

        // [2] D-09: SHA256 sobre as bytes UTF-8 do conteúdo fornecido pelo LLM
        // (não há arquivo em disco ainda — vamos escrevê-lo). Cap preview em 50KB.
        var contentBytes = Encoding.UTF8.GetBytes(content);
        string contentHash = Convert.ToHexString(SHA256.HashData(contentBytes));
        string body = content;
        const int Cap = 50_000;
        if (body.Length > Cap) body = body.Substring(0, Cap) + "\n\n[... SCRIPT TRUNCATED]";

        // [3] Build context — Command surfaces destino para fechar Open Q#1.
        var ctx = new CommandConfirmationContext
        {
            Tool = "materialize_skill",
            Command = $"{interp} -> {destPath}",
            Level = userLevel,
            Cwd = Path.GetDirectoryName(destPath) ?? "",
            ScriptBody = body,
            Interpreter = interp,
            ContentHash = contentHash,
        };

        var allowKey = (ctx.Tool, ctx.Command, ctx.ContentHash);

        if (AlwaysAllowSession.Contains(allowKey))
        {
            _ = AuditLogService.AppendAsync(ModalAuditEntry.Build(ctx, "always_allow", true));
        }
        else
        {
            if (System.Windows.Application.Current == null)
            {
                _ = AuditLogService.AppendAsync(ModalAuditEntry.Build(ctx, "deny_no_ui", false));
                return "ACESSO NEGADO: interface de confirmação indisponível.";
            }

            var (allowed, alwaysAllow) = await CommandConfirmationWindow.ShowAsync(ctx);

            if (!allowed)
            {
                _ = AuditLogService.AppendAsync(ModalAuditEntry.Build(ctx, "deny", false));
                return "Comando recusado pelo usuário";
            }

            if (alwaysAllow)
            {
                AlwaysAllowSession.Add(allowKey);
                _ = AuditLogService.AppendAsync(ModalAuditEntry.Build(ctx, "always_allow", true));
            }
            else
            {
                _ = AuditLogService.AppendAsync(ModalAuditEntry.Build(ctx, "allow", false));
            }
        }

        return await SkillService.MaterializeSkillAsync(name, content, interp);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// FERRAMENTA: manage_clipboard — Lê ou Escreve no Clipboard
// ─────────────────────────────────────────────────────────────────────────────

public class ManageClipboardTool : ITool
{
    public string Name => "manage_clipboard";
    public string Description => "Lê ('read') ou grava ('write') conteúdo na área de transferência (clipboard).";
    public int RequiredLevel => 1;

    public ChatTool ChatToolDefinition => ChatTool.CreateFunctionTool(
        Name, Description,
        BinaryData.FromString("""
        {
          "type": "object",
          "properties": {
            "action": { "type": "string", "description": "'read' ou 'write'." },
            "text": { "type": "string", "description": "Se action='write', o texto a ser copiado." }
          },
          "required": ["action"]
        }
        """));

    public async Task<string> ExecuteAsync(string argumentsJson, int userLevel = 1)
    {
        string action = ToolArgParser.Get(argumentsJson, "action");
        string text = ToolArgParser.Get(argumentsJson, "text");

        try
        {
            if (action == "write")
            {
                if (userLevel < 6) return "ACESSO NEGADO: 'write' requer nível 6.";
                if (string.IsNullOrEmpty(text)) return "ERRO: 'text' é obrigatório para write.";

                await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    System.Windows.Clipboard.SetText(text);
                });
                return "Texto copiado com sucesso para a área de transferência.";
            }
            else if (action == "read")
            {
                return await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    if (System.Windows.Clipboard.ContainsText())
                    {
                        string content = System.Windows.Clipboard.GetText();
                        return string.IsNullOrWhiteSpace(content) ? "[CLIPBOARD VAZIO]" : content;
                    }
                    return "[NENHUM TEXTO NO CLIPBOARD]";
                });
            }
            
            return "ERRO: 'action' deve ser 'read' ou 'write'.";
        }
        catch (Exception ex)
        {
            return $"ERRO ao interagir com o clipboard: {ex.Message}";
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// FERRAMENTA: set_reminder — Cria lembrete
// ─────────────────────────────────────────────────────────────────────────────

public class SetReminderTool : ITool
{
    public string Name => "set_reminder";
    public string Description => "Agenda um lembrete ou alerta para o usuário.";
    public int RequiredLevel => 1;

    public ChatTool ChatToolDefinition => ChatTool.CreateFunctionTool(
        Name, Description,
        BinaryData.FromString("""
        {
          "type": "object",
          "properties": {
            "message": { "type": "string", "description": "A mensagem do lembrete." },
            "delay_minutes": { "type": "integer", "description": "Opcional. Daqui a quantos minutos." },
            "target_time": { "type": "string", "description": "Opcional. Horário exato para o lembrete (HH:mm)." }
          },
          "required": ["message"]
        }
        """));

    public Task<string> ExecuteAsync(string argumentsJson, int userLevel = 1)
    {
        string message = ToolArgParser.Get(argumentsJson, "message");
        string delayStr = ToolArgParser.Get(argumentsJson, "delay_minutes");
        string targetTimeStr = ToolArgParser.Get(argumentsJson, "target_time");
        
        if (string.IsNullOrWhiteSpace(message))
            return Task.FromResult("ERRO: 'message' é obrigatório.");

        int delayMinutes = 0;

        if (!string.IsNullOrWhiteSpace(targetTimeStr) && DateTime.TryParseExact(targetTimeStr, "HH:mm", null, System.Globalization.DateTimeStyles.None, out DateTime targetTime))
        {
            var now = DateTime.Now;
            var target = new DateTime(now.Year, now.Month, now.Day, targetTime.Hour, targetTime.Minute, 0);
            
            if (target < now) target = target.AddDays(1);
            
            delayMinutes = (int)Math.Round((target - now).TotalMinutes);
        }
        else if (int.TryParse(delayStr, out int parsedDelay))
        {
            delayMinutes = parsedDelay;
        }
        else
        {
            return Task.FromResult("ERRO: Forneça 'target_time' ou 'delay_minutes'.");
        }

        return Task.FromResult(ReminderService.AddReminder(message, delayMinutes));
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// FERRAMENTA: glob — Encontra arquivos por padrão (wildcards)
// ─────────────────────────────────────────────────────────────────────────────

public class GlobTool : ITool
{
    public string Name => "glob";
    public string Description => "Encontra arquivos por padrão de wildcard (ex: '*.cs', 'temp*.log', 'Settings*.xaml'). Use ANTES de read_file quando não souber o caminho exato.";
    public int RequiredLevel => 1;

    public ChatTool ChatToolDefinition => ChatTool.CreateFunctionTool(
        Name, Description,
        BinaryData.FromString("""
        {
          "type": "object",
          "properties": {
            "pattern":     { "type": "string", "description": "Padrão de wildcard (ex: '*.cs', 'temp*.log'). Use simples wildcards, sem ** (use 'recursive' para varrer subpastas)." },
            "directory":   { "type": "string", "description": "Caminho absoluto onde buscar. Default: pasta do usuário." },
            "recursive":   { "type": "boolean", "description": "Se true, busca em subpastas. Default: true." },
            "max_results": { "type": "integer", "description": "Limite de arquivos retornados. Default: 50." }
          },
          "required": ["pattern"]
        }
        """));

    public async Task<string> ExecuteAsync(string argumentsJson, int userLevel = 1)
    {
        string pattern = ToolArgParser.Get(argumentsJson, "pattern");
        string directory = ToolArgParser.Get(argumentsJson, "directory");
        string recursiveStr = ToolArgParser.Get(argumentsJson, "recursive");
        string maxResultsStr = ToolArgParser.Get(argumentsJson, "max_results");

        if (string.IsNullOrWhiteSpace(pattern)) return "ERRO: 'pattern' é obrigatório.";
        // Limpa padrões estilo glob avançado que Directory.EnumerateFiles não entende
        if (pattern.StartsWith("**/")) pattern = pattern.Substring(3);
        if (pattern.StartsWith("**\\")) pattern = pattern.Substring(3);

        if (string.IsNullOrWhiteSpace(directory))
            directory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        directory = directory.Trim('\"', '\'');

        bool recursive = string.IsNullOrEmpty(recursiveStr) || !bool.TryParse(recursiveStr, out var r) || r;
        int maxResults = int.TryParse(maxResultsStr, out var m) ? Math.Max(1, Math.Min(m, 500)) : 50;

        if (!Directory.Exists(directory)) return $"ERRO: Diretório não encontrado: {directory}";

        // Sandbox por nível (igual RunCommandTool): níveis baixos restritos a pastas do usuário
        if (userLevel < 4)
        {
            string dirLower = directory.ToLowerInvariant();
            string[] sysDirs = { "\\windows", "\\program files", "\\programdata" };
            if (sysDirs.Any(d => dirLower.Contains(d)))
                return "ACESSO NEGADO (SANDBOX): Diretórios de sistema protegidos.";
        }

        try
        {
            var opt = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            var matches = await Task.Run(() =>
                Directory.EnumerateFiles(directory, pattern, opt)
                    .Take(maxResults + 1)
                    .ToList());

            bool truncated = matches.Count > maxResults;
            if (truncated) matches = matches.Take(maxResults).ToList();

            if (matches.Count == 0)
                return $"Nenhum arquivo encontrado em '{directory}' com padrão '{pattern}' (recursive={recursive}).";

            var sb = new StringBuilder();
            sb.AppendLine($"[GLOB] {matches.Count} arquivo(s){(truncated ? "+" : "")} encontrado(s):");
            foreach (var path in matches) sb.AppendLine(path);
            if (truncated) sb.AppendLine($"\n[AVISO: limite de {maxResults} atingido — pode haver mais arquivos. Refine o padrão ou aumente max_results.]");
            return sb.ToString();
        }
        catch (UnauthorizedAccessException ex) { return $"ERRO de permissão: {ex.Message}"; }
        catch (Exception ex) { return $"ERRO no glob: {ex.Message}"; }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// FERRAMENTA: grep — Busca regex em arquivos texto
// ─────────────────────────────────────────────────────────────────────────────

public class GrepTool : ITool
{
    public string Name => "grep";
    public string Description => "Busca padrão (regex ou texto) em arquivos texto. Retorna matches no formato 'arquivo:linha: trecho'. Use para localizar referências antes de ler arquivos inteiros.";
    public int RequiredLevel => 2;

    // Extensões que tratamos como texto (evita ler binários enormes)
    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".cs", ".csproj", ".sln", ".xaml", ".json", ".yaml", ".yml",
        ".xml", ".html", ".htm", ".css", ".js", ".ts", ".py", ".sh", ".ps1", ".bat",
        ".log", ".ini", ".cfg", ".conf", ".env", ".gitignore", ".sql", ".http", ".rest"
    };

    public ChatTool ChatToolDefinition => ChatTool.CreateFunctionTool(
        Name, Description,
        BinaryData.FromString("""
        {
          "type": "object",
          "properties": {
            "pattern":               { "type": "string", "description": "Texto literal ou regex .NET a buscar." },
            "directory":             { "type": "string", "description": "Caminho absoluto. Default: pasta do usuário." },
            "file_pattern":          { "type": "string", "description": "Filtro de arquivos (ex: '*.cs'). Default: '*'." },
            "case_sensitive":        { "type": "boolean", "description": "Default: false." },
            "regex":                 { "type": "boolean", "description": "Se true, pattern é tratado como regex; senão, texto literal. Default: false." },
            "max_files":             { "type": "integer", "description": "Máximo de arquivos varridos. Default: 100." },
            "max_matches_per_file":  { "type": "integer", "description": "Máximo de matches por arquivo. Default: 10." }
          },
          "required": ["pattern"]
        }
        """));

    public async Task<string> ExecuteAsync(string argumentsJson, int userLevel = 1)
    {
        string pattern = ToolArgParser.Get(argumentsJson, "pattern");
        string directory = ToolArgParser.Get(argumentsJson, "directory");
        string filePattern = ToolArgParser.Get(argumentsJson, "file_pattern");
        string caseSensitiveStr = ToolArgParser.Get(argumentsJson, "case_sensitive");
        string regexStr = ToolArgParser.Get(argumentsJson, "regex");
        string maxFilesStr = ToolArgParser.Get(argumentsJson, "max_files");
        string maxMatchesPerFileStr = ToolArgParser.Get(argumentsJson, "max_matches_per_file");

        if (string.IsNullOrWhiteSpace(pattern)) return "ERRO: 'pattern' é obrigatório.";

        if (string.IsNullOrWhiteSpace(directory))
            directory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        directory = directory.Trim('\"', '\'');
        if (!Directory.Exists(directory)) return $"ERRO: Diretório não encontrado: {directory}";

        if (string.IsNullOrWhiteSpace(filePattern)) filePattern = "*";
        bool caseSensitive = bool.TryParse(caseSensitiveStr, out var cs) && cs;
        bool useRegex = bool.TryParse(regexStr, out var ur) && ur;
        int maxFiles = int.TryParse(maxFilesStr, out var mf) ? Math.Max(1, Math.Min(mf, 500)) : 100;
        int maxMatchesPerFile = int.TryParse(maxMatchesPerFileStr, out var mm) ? Math.Max(1, Math.Min(mm, 50)) : 10;

        // Sandbox idêntica ao Glob
        if (userLevel < 4)
        {
            string dirLower = directory.ToLowerInvariant();
            string[] sysDirs = { "\\windows", "\\program files", "\\programdata" };
            if (sysDirs.Any(d => dirLower.Contains(d)))
                return "ACESSO NEGADO (SANDBOX): Diretórios de sistema protegidos.";
        }

        // Compila regex (ou escapa pattern literal para virar regex segura)
        System.Text.RegularExpressions.Regex re;
        try
        {
            string regexPattern = useRegex ? pattern : System.Text.RegularExpressions.Regex.Escape(pattern);
            var opts = caseSensitive
                ? System.Text.RegularExpressions.RegexOptions.Compiled
                : System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase;
            re = new System.Text.RegularExpressions.Regex(regexPattern, opts);
        }
        catch (Exception ex) { return $"ERRO ao compilar regex: {ex.Message}"; }

        try
        {
            return await Task.Run(() =>
            {
                var sb = new StringBuilder();
                int totalMatches = 0;
                int filesScanned = 0;
                int filesWithMatches = 0;

                IEnumerable<string> files;
                try
                {
                    files = Directory.EnumerateFiles(directory, filePattern, SearchOption.AllDirectories);
                }
                catch (Exception ex) { return $"ERRO ao listar arquivos: {ex.Message}"; }

                foreach (var file in files)
                {
                    if (filesScanned >= maxFiles) break;

                    // Pula binários por extensão
                    string ext = Path.GetExtension(file).ToLowerInvariant();
                    if (!string.IsNullOrEmpty(ext) && !TextExtensions.Contains(ext)) continue;

                    filesScanned++;
                    try
                    {
                        var lines = File.ReadAllLines(file);
                        int inFile = 0;
                        for (int i = 0; i < lines.Length; i++)
                        {
                            if (re.IsMatch(lines[i]))
                            {
                                string trimmed = lines[i].Trim();
                                if (trimmed.Length > 240) trimmed = trimmed.Substring(0, 237) + "...";
                                sb.AppendLine($"{file}:{i + 1}: {trimmed}");
                                inFile++;
                                totalMatches++;
                                if (inFile >= maxMatchesPerFile)
                                {
                                    sb.AppendLine($"  [...mais matches em {file} omitidos]");
                                    break;
                                }
                            }
                        }
                        if (inFile > 0) filesWithMatches++;
                    }
                    catch { /* arquivo inacessível ou binário disfarçado, skip */ }
                }

                if (totalMatches == 0)
                    return $"Nenhum match para '{pattern}' em {filesScanned} arquivo(s) varridos.";

                var header = new StringBuilder();
                header.AppendLine($"[GREP] {totalMatches} match(es) em {filesWithMatches}/{filesScanned} arquivo(s):");
                header.Append(sb);
                if (filesScanned >= maxFiles) header.AppendLine($"\n[AVISO: limite de {maxFiles} arquivos varridos atingido]");
                return header.ToString();
            });
        }
        catch (Exception ex) { return $"ERRO no grep: {ex.Message}"; }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// FERRAMENTA: list_dir — Lista conteúdo de um diretório
// ─────────────────────────────────────────────────────────────────────────────

public class ListDirTool : ITool
{
    public string Name => "list_dir";
    public string Description => "Lista conteúdo de um diretório (pastas + arquivos com tamanhos). Para varrer recursivamente, use 'glob' em vez disso.";
    public int RequiredLevel => 1;

    public ChatTool ChatToolDefinition => ChatTool.CreateFunctionTool(
        Name, Description,
        BinaryData.FromString("""
        {
          "type": "object",
          "properties": {
            "path":         { "type": "string", "description": "Caminho absoluto do diretório a listar." },
            "show_hidden":  { "type": "boolean", "description": "Inclui itens ocultos (que começam com '.'). Default: false." }
          },
          "required": ["path"]
        }
        """));

    public Task<string> ExecuteAsync(string argumentsJson, int userLevel = 1)
    {
        string path = ToolArgParser.Get(argumentsJson, "path");
        string showHiddenStr = ToolArgParser.Get(argumentsJson, "show_hidden");

        if (string.IsNullOrWhiteSpace(path)) return Task.FromResult("ERRO: 'path' é obrigatório.");
        path = path.Trim('\"', '\'');
        if (!Directory.Exists(path)) return Task.FromResult($"ERRO: Diretório não encontrado: {path}");

        bool showHidden = bool.TryParse(showHiddenStr, out var sh) && sh;

        // Sandbox: níveis baixos não podem listar áreas de sistema
        if (userLevel < 4)
        {
            string pathLower = path.ToLowerInvariant();
            string[] sysDirs = { "\\windows", "\\program files", "\\programdata" };
            if (sysDirs.Any(d => pathLower.Contains(d)))
                return Task.FromResult("ACESSO NEGADO (SANDBOX): Diretórios de sistema protegidos.");
        }

        try
        {
            var sb = new StringBuilder();
            sb.AppendLine($"[LIST_DIR] {path}");

            var dirs = Directory.EnumerateDirectories(path)
                .Where(d => showHidden || !Path.GetFileName(d).StartsWith("."))
                .OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var files = Directory.EnumerateFiles(path)
                .Where(f => showHidden || !Path.GetFileName(f).StartsWith("."))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();

            sb.AppendLine($"({dirs.Count} pasta(s), {files.Count} arquivo(s))\n");

            foreach (var d in dirs)
                sb.AppendLine($"📁 {Path.GetFileName(d)}/");

            foreach (var f in files)
            {
                try
                {
                    var info = new FileInfo(f);
                    sb.AppendLine($"📄 {Path.GetFileName(f)} ({FormatSize(info.Length)})");
                }
                catch
                {
                    sb.AppendLine($"📄 {Path.GetFileName(f)} (tamanho indisponível)");
                }
            }

            return Task.FromResult(sb.ToString());
        }
        catch (UnauthorizedAccessException ex) { return Task.FromResult($"ERRO de permissão: {ex.Message}"); }
        catch (Exception ex) { return Task.FromResult($"ERRO ao listar: {ex.Message}"); }
    }

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes}B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1}KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1}MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F2}GB";
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// FERRAMENTA: write_file — Escreve em arquivos
// ─────────────────────────────────────────────────────────────────────────────

public class WriteFileTool : ITool
{
    public string Name => "write_file";
    public string Description => "Cria um novo arquivo ou sobrescreve um existente com o conteúdo fornecido. O caminho absoluto e o diretório pai serão criados se não existirem.";
    public int RequiredLevel => 2;

    public ChatTool ChatToolDefinition => ChatTool.CreateFunctionTool(
        Name, Description,
        BinaryData.FromString("""
        {
          "type": "object",
          "properties": {
            "absolute_path": { "type": "string", "description": "OBRIGATÓRIO: O caminho absoluto do arquivo." },
            "content": { "type": "string", "description": "O código bruto 100% completo. NUNCA abrevie código com reticências. NUNCA use tags HTML (<style>, <script>) ou crases (```) em arquivos CSS/JS. O conteúdo deve estar pronto para o compilador/interpretador." }
          },
          "required": ["absolute_path", "content"]
        }
        """));

    public async Task<string> ExecuteAsync(string argumentsJson, int userLevel = 1)
    {
        string path = ToolArgParser.Get(argumentsJson, "absolute_path");
        string content = ToolArgParser.Get(argumentsJson, "content");
        
        if (string.IsNullOrWhiteSpace(path)) return "ERRO: 'absolute_path' é obrigatório. Você provavelmente tentou enviar um arquivo grande e se esqueceu desta propriedade.";
        
        path = path.Trim('\"', '\'');
        
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
            
            await File.WriteAllTextAsync(path, content, System.Text.Encoding.UTF8);
            return $"SUCESSO: Arquivo salvo com sucesso em {path} ({content.Length} caracteres).";
        }
        catch (Exception ex)
        {
            return $"ERRO ao escrever arquivo: {ex.Message}";
        }
    }
}
