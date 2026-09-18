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
    /// <summary>
    /// Teto de tempo: skill que trava não segura o turno. O dobro do shell (30s), porque uma
    /// skill costuma fazer mais que um comando avulso.
    /// </summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    private const int MaxSaida = 8000;

    /// <summary>
    /// Habilidades cujo manual já foi mandado ao modelo. Uma vez basta.
    /// <para>
    /// O manual tem quase mil caracteres. Em produção ele foi anexado a QUATRO falhas seguidas
    /// da mesma habilidade — quatro mil caracteres do mesmo texto empurrados para dentro do
    /// contexto, num prompt que já estava perto do ponto em que o modelo começou a errar
    /// sintaxe e a emitir chamada de ferramenta como texto. A ajuda estava alimentando o
    /// problema que ela existia para resolver.
    /// </para>
    /// <para>
    /// Zerado quando a habilidade finalmente roda: se ela voltar a falhar depois de um sucesso,
    /// o assunto é outro e o manual volta a valer.
    /// </para>
    /// </summary>
    private readonly System.Collections.Generic.HashSet<string> _manualEnviado =
        new(StringComparer.OrdinalIgnoreCase);

    public string Name => Ferramentas.Habilidade;

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
            // que não existe, e responder "sim" não executaria nada. Recusa aqui devolvendo null,
            // e o registry responde ao modelo com o "ACESSO NEGADO" genérico de quem não
            // conseguiu descrever a operação — sem a lista do que existe.
            if (skill == null || skill.ScriptPath.Length == 0) return null;

            return new CommandConfirmationContext
            {
                Tool = Name,
                Command = string.IsNullOrWhiteSpace(extra)
                    ? skill.ScriptPath
                    : $"{skill.ScriptPath} {extra}",
                Level = userLevel,
                Cwd = skill.Folder
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

    /// <summary>
    /// Confere o caminho citado nos argumentos antes de gastar um cartão e um turno com uma
    /// chamada que não tem como funcionar. Ver <see cref="PreVooDeCaminho"/>.
    /// </summary>
    public string? Validar(string argumentsJson)
    {
        try
        {
            var args = JsonSerializer.Deserialize<JsonElement>(argumentsJson);

            string nome = args.TryGetProperty("skill_name", out var n) ? n.GetString() ?? "" : "";
            string extra = args.TryGetProperty("arguments", out var a) ? a.GetString() ?? "" : "";

            if (extra.Length == 0) return null;

            var skill = SkillService.Find(nome);

            // Habilidade inexistente é problema do ExecuteAsync, que já responde com a lista do
            // que existe. Aqui só se confere caminho.
            return PreVooDeCaminho.Conferir(extra, skill?.Accepts);
        }
        catch (JsonException)
        {
            return null;
        }
    }

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

    private async Task<string> RodarAsync(LocalSkill skill, string argumentos)
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

            // Entrada redirecionada e FECHADA logo apos o Start. Sem isto o filho herda o
            // console do app, e um script com parametro obrigatorio ausente abre o prompt
            // "Supply values for the following parameters" e fica parado ate o teto de 60s.
            // Era o que acontecia aqui: a llm chamava a skill sem -Path, esperava um minuto
            // e recebia "passou de 60 segundos" - uma mensagem que nao diz o que faltou.
            // Com a entrada fechada o prompt le EOF e o erro volta na hora, nomeando o
            // parametro ausente.
            RedirectStandardInput = true,
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

            try { processo.StandardInput.Close(); } catch { }

            // Os dois canos lidos em PARALELO, pelo mesmo motivo do shell: ler um até o
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

            if (texto.Length > MaxSaida)
                texto = texto.Substring(0, MaxSaida) + "\n...[Saída truncada devido ao tamanho máximo].";

            // O corpo do SKILL.md so vai ao modelo QUANDO A CHAMADA FALHA, e e para isso
            // que ele serve: o prompt de sistema lista nome e descricao, nada sobre os
            // argumentos. Sem isto o modelo tinha de adivinhar a assinatura, errava, e
            // recebia de volta um erro sem nenhuma pista da forma certa - foram tres
            // tentativas cegas seguidas. Mandar as instrucoes sempre custaria contexto em
            // toda chamada bem-sucedida; manda-las no erro custa so quando servem.
            if (processo.ExitCode == 0)
            {
                // Funcionou: se falhar de novo mais tarde, o assunto é outro e o manual volta.
                _manualEnviado.Remove(skill.Name);
            }
            else if (skill.Instructions.Length > 0)
            {
                texto += _manualEnviado.Add(skill.Name)
                    ? $"\n\n--- Como usar a habilidade '{skill.Name}' ---\n{skill.Instructions}"
                    : $"\n\n(o manual de '{skill.Name}' já foi enviado nesta sessão — releia acima "
                      + "em vez de repetir a mesma chamada.)";
            }

            if (texto.Length == 0)
            {
                return processo.ExitCode == 0
                    ? $"Habilidade '{skill.Name}' executada (sem saída)."
                    : $"ERRO: a habilidade '{skill.Name}' terminou com código {processo.ExitCode} e nao escreveu nada.";
            }

            return texto;
        }
        catch (Exception ex)
        {
            return $"ERRO ao executar a habilidade '{skill.Name}': {ex.Message}";
        }
    }
}
