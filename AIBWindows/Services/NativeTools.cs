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

namespace AIB.Services;

// ─────────────────────────────────────────────────────────────────────────────
// UTILITÁRIO: Helper de extração de argumentos JSON com auto-reparo
// ─────────────────────────────────────────────────────────────────────────────

internal static class ToolArgParser
{
    /// <summary>
    /// Tenta extrair uma propriedade string de um JSON. Se o parse falhar,
    /// tenta um fallback via busca textual simples para tolerar JSONs malformados
    /// gerados por modelos locais menores.
    /// </summary>
    internal static string Get(string json, string key)
    {
        // 1. Parse nativo (caminho feliz)
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty(key, out var val))
                return val.GetString() ?? string.Empty;
        }
        catch { /* Falha silenciosa, tenta fallback */ }

        // 2. Fallback: sanitização básica (remove lixo antes do '{' e depois do '}')
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
        catch { /* Falha silenciosa, tenta regex */ }

        // 3. Último recurso: extração textual via busca de padrão "key":"value"
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
// FERRAMENTA: remember — Memoriza fatos relevantes em arquivos locais
// ─────────────────────────────────────────────────────────────────────────────

public class RememberTool : ITool
{
    public string Name => "remember";
    public string Description => "Salva uma informação importante na memória de longo prazo.";
    public int RequiredLevel => 1;

    public ChatTool ChatToolDefinition => ChatTool.CreateFunctionTool(
        Name, Description,
        BinaryData.FromString("""
        {
          "type": "object",
          "properties": {
            "key": {
              "type": "string",
              "description": "Identificador curto e descritivo do fato (ex: 'nome_usuario', 'empresa_cliente')."
            },
            "info": {
              "type": "string",
              "description": "O conteúdo completo do fato a ser memorizado."
            }
          },
          "required": ["key", "info"]
        }
        """));

    public async Task<string> ExecuteAsync(string argumentsJson, int userLevel = 1)
    {
        string key = ToolArgParser.Get(argumentsJson, "key");
        string info = ToolArgParser.Get(argumentsJson, "info");
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(info))
            return "ERRO: 'key' e 'info' são obrigatórios.";
        return await MemoryService.RememberAsync(key, info);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// FERRAMENTA: recall — Busca na memória persistente local
// ─────────────────────────────────────────────────────────────────────────────

public class RecallTool : ITool
{
    public string Name => "recall";
    public string Description => "Recupera todas as informações armazenadas na memória de longo prazo.";
    public int RequiredLevel => 1;

    public ChatTool ChatToolDefinition => ChatTool.CreateFunctionTool(
        Name, Description,
        BinaryData.FromString("""
        {
          "type": "object",
          "properties": {
            "query": {
              "type": "string",
              "description": "Palavra-chave ou tema do que deseja buscar na memória."
            }
          },
          "required": ["query"]
        }
        """));

    public Task<string> ExecuteAsync(string argumentsJson, int userLevel = 1)
    {
        string query = ToolArgParser.Get(argumentsJson, "query");
        if (string.IsNullOrWhiteSpace(query)) return Task.FromResult("ERRO: 'query' é obrigatório.");
        return Task.FromResult(MemoryService.Recall(query));
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// FERRAMENTA: store_credential — Armazena credenciais via DPAPI
// ─────────────────────────────────────────────────────────────────────────────

public class StoreCredentialTool : ITool
{
    public string Name => "manage_vault";
    public string Description => "Guarda ou atualiza uma credencial sensível no cofre nativo criptografado do Windows (DPAPI). Apenas senhas, chaves de API e tokens.";
    public int RequiredLevel => 7;

    public ChatTool ChatToolDefinition => ChatTool.CreateFunctionTool(
        Name, Description,
        BinaryData.FromString("""
        {
          "type": "object",
          "properties": {
            "system": { "type": "string", "description": "Nome do sistema/serviço (ex: 'bitrix', 'openai')." },
            "key":    { "type": "string", "description": "Nome da chave (ex: 'api_key', 'token')." },
            "value":  { "type": "string", "description": "O valor secreto a armazenar." }
          },
          "required": ["system", "key", "value"]
        }
        """));

    public async Task<string> ExecuteAsync(string argumentsJson, int userLevel = 1)
    {
        string system = ToolArgParser.Get(argumentsJson, "system");
        string key = ToolArgParser.Get(argumentsJson, "key");
        string value = ToolArgParser.Get(argumentsJson, "value");
        if (string.IsNullOrWhiteSpace(system) || string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value))
            return "ERRO: 'system', 'key' e 'value' são obrigatórios.";
        return await CredentialService.StoreCredentialAsync(system, key, value);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// FERRAMENTA: retrieve_credential — Recupera credenciais do cofre
// ─────────────────────────────────────────────────────────────────────────────

public class RetrieveCredentialTool : ITool
{
    public string Name => "read_vault";
    public string Description => "Recupera uma credencial armazenada no cofre local criptografado. Use ANTES de qualquer integração que exija API key ou token.";
    public int RequiredLevel => 7;

    public ChatTool ChatToolDefinition => ChatTool.CreateFunctionTool(
        Name, Description,
        BinaryData.FromString("""
        {
          "type": "object",
          "properties": {
            "system": { "type": "string", "description": "Nome do sistema (ex: 'bitrix', 'openai')." },
            "key":    { "type": "string", "description": "Nome da chave a recuperar." }
          },
          "required": ["system", "key"]
        }
        """));

    public Task<string> ExecuteAsync(string argumentsJson, int userLevel = 1)
    {
        string system = ToolArgParser.Get(argumentsJson, "system");
        string key = ToolArgParser.Get(argumentsJson, "key");
        if (string.IsNullOrWhiteSpace(system) || string.IsNullOrWhiteSpace(key))
            return Task.FromResult("ERRO: 'system' e 'key' são obrigatórios.");
        return Task.FromResult(CredentialService.RetrieveCredential(system, key));
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// FERRAMENTA: read_file — Lê arquivos de texto, PDF, DOCX e XLSX
// ─────────────────────────────────────────────────────────────────────────────

public class ReadFileTool : ITool
{
    public string Name => "read_file";
    public string Description => "Lê o conteúdo de um arquivo do sistema (texto, .pdf, .docx, .xlsx). Use para evitar erros de terminal com caminhos complexos. Aceita apenas caminhos absolutos.";
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
                
                // Fallback para texto comum
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
                sb.AppendLine("---"); // Separador de abas/planilhas
            }
        }
        return sb.ToString();
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// FERRAMENTA: run_command — Executa comandos no terminal local
// ─────────────────────────────────────────────────────────────────────────────

public class RunCommandTool : ITool
{
    public string Name => "windows_console_execution";
    public string Description => "Executa comandos no terminal do usuário (PowerShell) e retorna a saída. Para caminhos longos com espaço, use aspas. Ex: ls 'C:\\Meu Caminho\\'";
    public int RequiredLevel => 2; // Bloqueios finos serão aplicados dentro do ExecuteAsync

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
        string command = ToolArgParser.Get(argumentsJson, "command");
        if (string.IsNullOrWhiteSpace(command)) return "ERRO: 'command' é obrigatório.";

        // --- INÍCIO DO SANDBOX ---
        if (userLevel < 9)
        {
            string cmdLower = command.ToLowerInvariant();
            
            // 1. Hard-block de sistema
            string[] sysDirs = { "appdata", "windows", "program files", "programdata" };
            if (sysDirs.Any(d => cmdLower.Contains(d)))
                return "ACESSO NEGADO (SANDBOX): Diretórios de sistema protegidos.";

            // 2. Confinamento de Diretórios (Níveis Baixos)
            if (userLevel <= 4)
            {
                // Se tentou usar caminhos absolutos fora das áreas permitidas
                if (cmdLower.Contains("c:\\") || cmdLower.Contains("d:\\"))
                {
                    bool allow = false;
                    if (cmdLower.Contains("documents") || cmdLower.Contains("documentos")) allow = true;
                    if (userLevel >= 3 && (cmdLower.Contains("downloads"))) allow = true;
                    if (!allow) return $"ACESSO NEGADO (SANDBOX): Nível {userLevel} restrito à Documentos/Downloads.";
                }

                if (userLevel <= 2 && (cmdLower.Contains("ls ") || cmdLower.Contains("dir ")))
                    return "ACESSO NEGADO (SANDBOX): Listagem em massa bloqueada no Nível 2. Tente usar Select-String ou Get-Content em um arquivo exato.";
            }

            // 3. Bloqueio Destrutivo (Nível < 8)
            if (userLevel < 8)
            {
                string[] destructives = { "rm ", "del ", "remove-item", "out-file", "set-content", "new-item", ">", ">>", "mkdir", "md " };
                if (destructives.Any(b => cmdLower.Contains(b)))
                    return "ACESSO NEGADO (SANDBOX): Comandos de gravação, criação ou exclusão requerem Nível 8.";
            }

            // 4. Bloqueio de Rede (Nível < 7)
            if (userLevel < 7)
            {
                string[] netCmds = { "curl", "wget", "invoke-webrequest", "ping" };
                if (netCmds.Any(b => cmdLower.Contains(b)))
                    return "ACESSO NEGADO (SANDBOX): Comandos de rede requerem Nível 7.";
            }
        }
        // --- FIM DO SANDBOX ---

        return await CommandService.ExecuteAsync(command, DirectoryService.DataDir);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// FERRAMENTA: search_web — Pesquisa na internet via DuckDuckGo
// ─────────────────────────────────────────────────────────────────────────────

public class SearchWebTool : ITool
{
    public string Name => "search_web";
    public string Description => "Pesquisa na internet em tempo real usando DuckDuckGo. Ideal para buscar informações atualizadas, eventos recentes ou dados que não estão na memória.";
    public int RequiredLevel => 3;

    public ChatTool ChatToolDefinition => ChatTool.CreateFunctionTool(
        Name, Description,
        BinaryData.FromString("""
        {
          "type": "object",
          "properties": {
            "query": {
              "type": "string",
              "description": "O termo ou pergunta a pesquisar na internet."
            }
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
// FERRAMENTA: ocr_screen — Extrai texto de todas as telas via OCR nativo
// ─────────────────────────────────────────────────────────────────────────────

public class OcrScreenTool : ITool
{
    public string Name => "ocr_screen";
    public string Description => "Captura e lê o texto de TODAS as telas conectadas usando o OCR nativo do Windows. Use quando o usuário pedir para ler, analisar ou descrever o que está na tela.";
    public int RequiredLevel => 4;

    public ChatTool ChatToolDefinition => ChatTool.CreateFunctionTool(
        Name, Description,
        BinaryData.FromString("""{ "type": "object", "properties": {} }"""));

    public async Task<string> ExecuteAsync(string argumentsJson, int userLevel = 1)
    {
        try
        {
            var ocr = new OcrService();
            string text = await ocr.ExtractTextFromAllScreensAsync();
            if (string.IsNullOrWhiteSpace(text))
                return "[OCR] Nenhum texto foi encontrado nas telas.";
            return $"[CONTEÚDO LIDO DAS TELAS VIA OCR]:\n\n{text}";
        }
        catch (Exception ex)
        {
            return $"ERRO no OCR: {ex.Message}";
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// FERRAMENTA: capture_screen — Captura screenshot panorâmica e descreve via OCR
// ─────────────────────────────────────────────────────────────────────────────

public class CaptureScreenTool : ITool
{
    public string Name => "capture_screen";
    public string Description => "Tira uma captura de tela panorâmica de todos os monitores e extrai o texto via OCR para fornecer contexto visual completo ao agente.";
    public int RequiredLevel => 3;

    public ChatTool ChatToolDefinition => ChatTool.CreateFunctionTool(
        Name, Description,
        BinaryData.FromString("""{ "type": "object", "properties": {} }"""));

    public async Task<string> ExecuteAsync(string argumentsJson, int userLevel = 1)
    {
        try
        {
            var ocr = new OcrService();
            string text = await ocr.ExtractTextFromAllScreensAsync();
            string context = string.IsNullOrWhiteSpace(text)
                ? "[Nenhum texto detectado na captura]"
                : text;
            return $"[CONTEXTO VISUAL DA TELA - OCR]:\n{context}";
        }
        catch (Exception ex)
        {
            return $"ERRO ao capturar tela: {ex.Message}";
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// FERRAMENTA: materialize_skill — Cria ou atualiza um script de skill local
// ─────────────────────────────────────────────────────────────────────────────

public class MaterializeSkillTool : ITool
{
    public string Name => "materialize_skill";
    public string Description => "Cria ou atualiza um script Python ou PowerShell na pasta de skills local. Após materializar, a skill fica disponível como uma ferramenta direta nas próximas conversas.";
    public int RequiredLevel => 9;

    public ChatTool ChatToolDefinition => ChatTool.CreateFunctionTool(
        Name, Description,
        BinaryData.FromString("""
        {
          "type": "object",
          "properties": {
            "skill_name":     { "type": "string", "description": "Nome único da skill (ex: 'consultar_bitrix')." },
            "script_content": { "type": "string", "description": "Conteúdo completo do script Python ou PowerShell." },
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
// FERRAMENTA: read_clipboard — Lê o conteúdo da área de transferência
// ─────────────────────────────────────────────────────────────────────────────

public class ClipboardReadTool : ITool
{
    public string Name => "read_clipboard";
    public string Description => "Lê o texto atual que está na área de transferência (Ctrl+C) do usuário.";
    public int RequiredLevel => 1;

    public ChatTool ChatToolDefinition => ChatTool.CreateFunctionTool(
        Name, Description,
        BinaryData.FromString("""{ "type": "object", "properties": {} }"""));

    public async Task<string> ExecuteAsync(string argumentsJson, int userLevel = 1)
    {
        try
        {
            return await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                if (System.Windows.Clipboard.ContainsText())
                {
                    string text = System.Windows.Clipboard.GetText();
                    return string.IsNullOrWhiteSpace(text) ? "[CLIPBOARD VAZIO]" : text;
                }
                return "[NENHUM TEXTO NO CLIPBOARD]";
            });
        }
        catch (Exception ex)
        {
            return $"ERRO ao ler clipboard: {ex.Message}";
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// FERRAMENTA: write_clipboard — Grava conteúdo na área de transferência
// ─────────────────────────────────────────────────────────────────────────────

public class ClipboardWriteTool : ITool
{
    public string Name => "write_clipboard";
    public string Description => "Copia um texto (código, resumo, etc.) diretamente para a área de transferência do usuário.";
    public int RequiredLevel => 6;

    public ChatTool ChatToolDefinition => ChatTool.CreateFunctionTool(
        Name, Description,
        BinaryData.FromString("""
        {
          "type": "object",
          "properties": {
            "text": { "type": "string", "description": "O texto a ser copiado para o clipboard." }
          },
          "required": ["text"]
        }
        """));

    public async Task<string> ExecuteAsync(string argumentsJson, int userLevel = 1)
    {
        string text = ToolArgParser.Get(argumentsJson, "text");
        if (string.IsNullOrEmpty(text)) return "ERRO: 'text' é obrigatório.";

        try
        {
            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                System.Windows.Clipboard.SetText(text);
            });
            return "Texto copiado com sucesso para a área de transferência.";
        }
        catch (Exception ex)
        {
            return $"ERRO ao gravar no clipboard: {ex.Message}";
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// FERRAMENTA: read_active_window — Obtém o título da janela ativa
// ─────────────────────────────────────────────────────────────────────────────

public class ActiveWindowTool : ITool
{
    public string Name => "read_active_window";
    public string Description => "Obtém o título da janela atualmente em foco no desktop do usuário. Útil para descobrir em qual site ou documento ele está trabalhando.";
    public int RequiredLevel => 3;

    public ChatTool ChatToolDefinition => ChatTool.CreateFunctionTool(
        Name, Description,
        BinaryData.FromString("""{ "type": "object", "properties": {} }"""));

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder text, int count);

    public Task<string> ExecuteAsync(string argumentsJson, int userLevel = 1)
    {
        try
        {
            IntPtr handle = GetForegroundWindow();
            if (handle == IntPtr.Zero) return Task.FromResult("[NENHUMA JANELA ATIVA]");
            
            var sb = new System.Text.StringBuilder(256);
            if (GetWindowText(handle, sb, 256) > 0)
            {
                return Task.FromResult(sb.ToString());
            }
            return Task.FromResult("[NÃO FOI POSSÍVEL LER O TÍTULO DA JANELA]");
        }
        catch (Exception ex)
        {
            return Task.FromResult($"ERRO ao ler janela ativa: {ex.Message}");
        }
    }
}
