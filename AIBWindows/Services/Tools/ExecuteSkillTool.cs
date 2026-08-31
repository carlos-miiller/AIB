using System;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OpenAI.Chat;

namespace AIB.Services.Tools;

/// <summary>
/// Roda uma habilidade instalada em <c>~/.AIB/skills</c>.
/// <para>
/// Existe para que capacidades novas não custem uma ferramenta nova no schema. Cada ferramenta
/// nativa é reenviada ao modelo em TODA requisição; uma skill custa uma linha de descrição no
/// prompt e nada mais até ser chamada.
/// </para>
/// <para>
/// Passa pelo portão de confirmação como o <see cref="RunCommandTool"/>, e pelo mesmo motivo:
/// executa código na máquina do usuário. O que o card mostra é o caminho literal do script —
/// autorizar uma skill é autorizar aquele arquivo, e o usuário tem de poder abri-lo antes de
/// dizer sim.
/// </para>
/// <para>
/// Limite conhecido: a floor list examina a LINHA DE COMANDO, não o conteúdo do script. Uma
/// skill que apague disco por dentro não é detectada por ela. A defesa aqui é a confirmação
/// nominal — o script está em disco, sob o nome que o usuário instalou, e o card diz qual é.
/// </para>
/// </summary>
public class ExecuteSkillTool : ITool
{
    /// <summary>Teto de tempo. O mesmo do run_command: skill que trava não segura o turno.</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    private const int MaxSaida = 8000;

    public string Name => "execute_skill";

    public string Description =>
        "Executa uma habilidade instalada pelo nome. Use quando a tarefa corresponder a uma das "
        + "habilidades listadas no prompt de sistema. Passe o nome exato em 'skill_name' e os "
        + "argumentos da habilidade em 'arguments'.";

    public int RequiredLevel => 2;

    public bool RequiresConfirmation => true;

    public CommandConfirmationContext? BuildConfirmationContext(string argumentsJson, int userLevel)
    {
        try
        {
            var args = JsonSerializer.Deserialize<JsonElement>(argumentsJson);

            string nome = args.TryGetProperty("skill_name", out var n) ? n.GetString() ?? "" : "";
            string extra = args.TryGetProperty("arguments", out var a) ? a.GetString() ?? "" : "";

            if (string.IsNullOrWhiteSpace(nome)) return null;

            var skill = SkillService.Find(nome);

            // Skill inexistente não vira pergunta: o usuário seria convidado a autorizar algo
            // que não existe, e responder "sim" não executaria nada. Recusa aqui, e o modelo
            // recebe a lista do que existe na mensagem de erro.
            if (skill == null || skill.ScriptPath.Length == 0) return null;

            return new CommandConfirmationContext
            {
                Tool = Name,
                Command = string.IsNullOrWhiteSpace(extra)
                    ? skill.ScriptPath
                    : $"{skill.ScriptPath} {extra}",
                Level = userLevel,
                Cwd = skill.Folder,
                DenylistHit = false,
                DenylistReason = ""
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public ChatTool ChatToolDefinition => ChatTool.CreateFunctionTool(
        functionName: Name,
        functionDescription: Description,
        functionParameters: BinaryData.FromString("""
        {
            "type": "object",
            "properties": {
                "skill_name": {
                    "type": "string",
                    "description": "Nome exato da habilidade, como aparece na lista do prompt de sistema."
                },
                "arguments": {
                    "type": "string",
                    "description": "Argumentos da habilidade, na forma que o SKILL.md dela documenta. Vazio quando ela não pede nenhum."
                }
            },
            "required": ["skill_name"]
        }
        """)
    );

    public async Task<string> ExecuteAsync(string argumentsJson, int userLevel = 1)
    {
        string nome;
        string extra;

        try
        {
            var args = JsonSerializer.Deserialize<JsonElement>(argumentsJson);
            nome = args.TryGetProperty("skill_name", out var n) ? n.GetString() ?? "" : "";
            extra = args.TryGetProperty("arguments", out var a) ? a.GetString() ?? "" : "";
        }
        catch (JsonException ex)
        {
            return $"ERRO: argumentos ilegíveis ({ex.Message}).";
        }

        if (string.IsNullOrWhiteSpace(nome))
            return "ERRO: O parâmetro 'skill_name' é obrigatório.";

        var skill = SkillService.Find(nome);
        if (skill == null)
        {
            var existentes = SkillService.ListLocalSkills();
            string lista = existentes.Count == 0
                ? "nenhuma habilidade instalada"
                : string.Join(", ", existentes.ConvertAll(s => s.Name));

            return $"ERRO: habilidade '{nome}' não encontrada. Disponíveis: {lista}.";
        }

        // Skill de documentação: não roda, ENTREGA o texto. É o caso de uma skill que ensina um
        // procedimento em vez de automatizá-lo.
        if (skill.Interpreter.Equals("markdown", StringComparison.OrdinalIgnoreCase)
            || skill.ScriptPath.Length == 0)
        {
            return skill.Instructions.Length > 0
                ? skill.Instructions
                : $"A habilidade '{skill.Name}' não tem script nem instruções.";
        }

        return await RodarAsync(skill, extra);
    }

    private static async Task<string> RodarAsync(LocalSkill skill, string argumentos)
    {
        var (executavel, prefixo) = skill.Interpreter.ToLowerInvariant() switch
        {
            "powershell" => ("powershell.exe", $"-NoProfile -ExecutionPolicy Bypass -File \"{skill.ScriptPath}\""),
            "python" => ("python", $"\"{skill.ScriptPath}\""),
            _ => ("", "")
        };

        if (executavel.Length == 0)
            return $"ERRO: interpretador '{skill.Interpreter}' não suportado. Use powershell, python ou markdown.";

        var inicio = new ProcessStartInfo
        {
            FileName = executavel,
            Arguments = string.IsNullOrWhiteSpace(argumentos) ? prefixo : $"{prefixo} {argumentos}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,

            // A pasta da skill: scripts com arquivos de apoio ao lado esperam encontrá-los por
            // caminho relativo.
            WorkingDirectory = skill.Folder
        };

        Console.WriteLine($"[SKILL] Executando '{skill.Name}': {inicio.FileName} {inicio.Arguments}");

        try
        {
            using var processo = new Process { StartInfo = inicio };
            processo.Start();

            // Os dois canos lidos em PARALELO, pelo mesmo motivo do run_command: ler um até o
            // fim antes do outro trava assim que o filho enche o buffer de 4KB do que sobrou.
            var saida = processo.StandardOutput.ReadToEndAsync();
            var erro = processo.StandardError.ReadToEndAsync();

            using var relogio = new CancellationTokenSource(Timeout);
            bool estourou = false;

            try { await processo.WaitForExitAsync(relogio.Token); }
            catch (OperationCanceledException) { estourou = true; }

            if (estourou)
            {
                try { processo.Kill(entireProcessTree: true); } catch { }
                try { await processo.WaitForExitAsync(); } catch { }
                await Task.WhenAny(Task.WhenAll(saida, erro), Task.Delay(2000));

                return $"ERRO: a habilidade '{skill.Name}' passou de {Timeout.TotalSeconds:0} segundos e foi interrompida.";
            }

            string texto = (await saida + "\n" + RunCommandTool.SemClixml(await erro)).Trim();

            if (texto.Length == 0) return $"Habilidade '{skill.Name}' executada (sem saída).";

            if (texto.Length > MaxSaida)
                texto = texto.Substring(0, MaxSaida) + "\n...[Saída truncada devido ao tamanho máximo].";

            return texto;
        }
        catch (Exception ex)
        {
            return $"ERRO ao executar a habilidade '{skill.Name}': {ex.Message}";
        }
    }
}
