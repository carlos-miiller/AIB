using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using OpenAI.Chat;
using UglyToad.PdfPig;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using DocumentFormat.OpenXml.Spreadsheet;
using System.Text;
using System.Runtime.InteropServices;

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

    // Helper: matcher por palavra ("rm ", "del", "ping") usando \b para evitar falsos positivos
    // como "firm" engatilhando "rm" ou "appending" engatilhando "ping".
    private static bool ContainsWord(string cmdLower, string token)
    {
        string trimmed = token.Trim();
        if (trimmed.Length == 0) return false;
        // Para tokens compostos por símbolo (ex: ">", ">>") usamos contains direto.
        if (!char.IsLetterOrDigit(trimmed[0]) && !char.IsLetterOrDigit(trimmed[^1]))
            return cmdLower.Contains(trimmed);
        string pattern = $@"(?<![A-Za-z0-9_-]){System.Text.RegularExpressions.Regex.Escape(trimmed)}(?![A-Za-z0-9_-])";
        return System.Text.RegularExpressions.Regex.IsMatch(cmdLower, pattern);
    }

    public async Task<string> ExecuteAsync(string argumentsJson, int userLevel = 1)
    {
        string command = ToolArgParser.Get(argumentsJson, "command");
        if (string.IsNullOrWhiteSpace(command)) return "ERRO: 'command' é obrigatório.";

        if (userLevel < 9)
        {
            string cmdLower = command.ToLowerInvariant();

            string[] sysDirs = { "appdata", "windows", "program files", "programdata" };
            if (sysDirs.Any(d => ContainsWord(cmdLower, d)))
                return "ACESSO NEGADO (SANDBOX): Diretórios de sistema protegidos.";

            if (userLevel <= 4)
            {
                if (cmdLower.Contains("c:\\") || cmdLower.Contains("d:\\"))
                {
                    bool allow = false;
                    if (ContainsWord(cmdLower, "documents") || ContainsWord(cmdLower, "documentos")) allow = true;
                    if (userLevel >= 3 && ContainsWord(cmdLower, "downloads")) allow = true;
                    if (!allow) return $"ACESSO NEGADO (SANDBOX): Nível {userLevel} restrito à Documentos/Downloads.";
                }

                if (userLevel <= 2 && (ContainsWord(cmdLower, "ls") || ContainsWord(cmdLower, "dir")))
                    return "ACESSO NEGADO (SANDBOX): Listagem em massa bloqueada no Nível 2.";
            }

            if (userLevel < 8)
            {
                string[] destructives = { "rm", "del", "erase", "remove-item", "ri", "out-file", "set-content", "add-content", "new-item", ">", ">>", "mkdir", "md", "rmdir", "rd", "format" };
                if (destructives.Any(b => ContainsWord(cmdLower, b)))
                    return "ACESSO NEGADO (SANDBOX): Comandos de gravação/exclusão requerem Nível 8.";
            }

            if (userLevel < 7)
            {
                string[] netCmds = { "curl", "wget", "invoke-webrequest", "iwr", "invoke-restmethod", "irm", "ping", "tracert", "nslookup", "ftp", "scp", "ssh" };
                if (netCmds.Any(b => ContainsWord(cmdLower, b)))
                    return "ACESSO NEGADO (SANDBOX): Comandos de rede requerem Nível 7.";
            }
        }

        return await CommandService.ExecuteAsync(command, DirectoryService.DataDir);
    }
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
    public string Description => "Captura o título da janela ativa E o texto completo de todas as telas (OCR).";
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
            string text = await ocr.ExtractTextFromAllScreensAsync();
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
    public int RequiredLevel => 1;

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
        
        return await SkillService.RunSkillAsync(skillName, args);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// FERRAMENTA: materialize_skill — Cria script de skill
// ─────────────────────────────────────────────────────────────────────────────

public class MaterializeSkillTool : ITool
{
    public string Name => "materialize_skill";
    public string Description => "Cria ou atualiza uma skill local (script).";
    public int RequiredLevel => 5;

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
