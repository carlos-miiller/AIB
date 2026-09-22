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

    /// <summary>
    /// O card mostra a linha de comando do script, e a floor list sabe lê-la. Ver
    /// <see cref="ITool.PassaPelaFloorList"/>.
    /// </summary>
    public bool PassaPelaFloorList => true;

    /// <summary>
    /// Ler o manual não é linha de comando: o Command é um caminho, e a floor list o leria como
    /// comando. Seguro porque o mesmo prefixo é o que <see cref="ExecutarAutorizadoAsync"/>
    /// confere — autorizado como manual, só o manual é entregue, nunca um script.
    /// </summary>
    public bool PassaPelaFloorListCom(CommandConfirmationContext contexto) =>
        !(contexto.Command ?? "").StartsWith(PrefixoDoManual, StringComparison.Ordinal);

    /// <summary>
    /// Skill de documentação não executa nada: só entrega o manual ao modelo. Pedir autorização
    /// para LER um texto que o próprio usuário instalou treina o clique em "Permitir" sem ler,
    /// e esvazia o portão onde ele importa. Antes, ela nem chegava a ser lida: o card não sabia
    /// descrevê-la, e o registry negava com "ACESSO NEGADO" genérico.
    /// <para>
    /// Com texto de e-mail no contexto o registry ignora esta dispensa, e o card aparece — por
    /// isso <see cref="BuildConfirmationContext"/> também sabe descrever a leitura do manual.
    /// </para>
    /// </summary>
    public bool DispensaConfirmacao(string argumentsJson)
    {
        var skill = SkillService.Find(Argumentos(argumentsJson)?.Nome);
        return skill != null && SoManual(skill);
    }

    /// <summary>
    /// Se a habilidade só entrega instruções: interpretador <c>markdown</c>, ou sem script em
    /// disco. É a MESMA condição que o <see cref="ExecuteAsync"/> usa para devolver o manual em
    /// vez de rodar — as duas não podem divergir, ou a dispensa valeria para algo que executa.
    /// </summary>
    private static bool SoManual(LocalSkill skill) =>
        skill.Interpreter.Equals("markdown", StringComparison.OrdinalIgnoreCase)
        || skill.ScriptPath.Length == 0;

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
            // que não existe. O Validar já recusou antes, com a lista do que existe; aqui é só a
            // segunda linha, para a skill que sumiu entre um e outro.
            if (skill == null) return null;

            // Só o manual: é o que o card descreve quando a dispensa não vale (e-mail no
            // contexto). Nada roda — a ExecuteAsync devolve o texto do SKILL.md.
            if (SoManual(skill))
            {
                return new CommandConfirmationContext
                {
                    Tool = Name,
                    Command = PrefixoDoManual + System.IO.Path.Combine(skill.Folder, "SKILL.md"),
                    Level = userLevel,
                    Cwd = skill.Folder
                };
            }

            return new CommandConfirmationContext
            {
                Tool = Name,
                Command = ComandoDoScript(skill, extra),
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
    /// Recusa antes do portão o que não tem como funcionar: nome ausente, habilidade que não
    /// existe (com a lista do que existe) e caminho citado nos argumentos que não existe. Ver
    /// <see cref="PreVooDeCaminho"/>.
    /// <para>
    /// A habilidade inexistente era deixada para o <see cref="ExecuteAsync"/>, que responderia
    /// com a lista — mas ele nunca chegava a rodar: o card não sabia descrever uma skill que não
    /// existe, e o registry negava antes com um "ACESSO NEGADO" genérico. O modelo ficava sem
    /// saber que o nome estava errado nem qual era o certo.
    /// </para>
    /// <para>
    /// JSON ilegível passa daqui sem recusa: o <see cref="BuildConfirmationContext"/> não o
    /// descreve, e o registry nega sem contexto. Não executa de um jeito nem de outro.
    /// </para>
    /// </summary>
    public string? Validar(string argumentsJson)
    {
        var a = Argumentos(argumentsJson);
        if (a == null) return null;

        if (string.IsNullOrWhiteSpace(a.Value.Nome))
            return "ERRO: O parâmetro 'skill_name' é obrigatório.";

        var skill = SkillService.Find(a.Value.Nome);
        if (skill == null) return NaoEncontrada(a.Value.Nome);

        // O manual não recebe argumento nenhum: não há caminho a conferir.
        if (SoManual(skill) || a.Value.Extra.Length == 0) return null;

        return PreVooDeCaminho.Conferir(a.Value.Extra, skill.Accepts);
    }

    /// <summary>"Não encontrada", com o que existe — é o que deixa o modelo se corrigir.</summary>
    private static string NaoEncontrada(string nome)
    {
        var existentes = SkillService.ListLocalSkills();
        string lista = existentes.Count == 0
            ? "nenhuma habilidade instalada"
            : string.Join(", ", existentes.ConvertAll(s => s.Name));

        return $"ERRO: habilidade '{nome}' não encontrada. Disponíveis: {lista}.";
    }

    /// <summary><c>skill_name</c> e <c>arguments</c>, ou null quando o JSON é ilegível.</summary>
    private static (string Nome, string Extra)? Argumentos(string argumentsJson)
    {
        try
        {
            var args = JsonSerializer.Deserialize<JsonElement>(argumentsJson);
            if (args.ValueKind != JsonValueKind.Object) return null;

            string nome = args.TryGetProperty("skill_name", out var n) && n.ValueKind == JsonValueKind.String
                ? n.GetString() ?? "" : "";
            string extra = args.TryGetProperty("arguments", out var a) && a.ValueKind == JsonValueKind.String
                ? a.GetString() ?? "" : "";

            return (nome, extra);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Começo do Command quando o que se autoriza é só ler o manual.</summary>
    public const string PrefixoDoManual = "LER MANUAL ";

    /// <summary>A linha que o card mostra para um script — e que a execução confere de novo.</summary>
    private static string ComandoDoScript(LocalSkill skill, string extra) =>
        string.IsNullOrWhiteSpace(extra) ? skill.ScriptPath : $"{skill.ScriptPath} {extra}";

    /// <summary>
    /// A execução pelo registry: roda só o que foi autorizado, e recusa se a habilidade mudou.
    /// <para>
    /// A brecha que isto fecha: uma skill de documentação é dispensada do card porque só lê o
    /// manual. Se o SKILL.md ganhasse um script entre a dispensa e a execução — outra chamada do
    /// mesmo lote gravando na pasta de skills, por exemplo —, o <see cref="ExecuteAsync"/>
    /// encontraria o script e o rodaria SEM card nenhum. Vale igual para o card que aprovou
    /// "LER MANUAL", e para o script aprovado que trocou de arquivo enquanto o card esperava.
    /// </para>
    /// <para>
    /// Sem estado guardado entre chamadas: a autorização vem na mão, e nada cresce nem vaza.
    /// Na dúvida — sem contexto, contexto de outra forma, linha diferente —, o script não roda.
    /// </para>
    /// </summary>
    public async Task<string> ExecutarAutorizadoAsync(
        string argumentsJson, int userLevel, CommandConfirmationContext? autorizado)
    {
        const string Mudou =
            "ERRO: a habilidade mudou desde a autorização e não foi executada. Chame de novo para "
            + "o usuário ver o que ela faz agora.";

        if (autorizado == null)
            return "ERRO: a habilidade não foi executada — não há autorização descrita para ela.";

        var a = Argumentos(argumentsJson);
        if (a == null) return "ERRO: argumentos ilegíveis.";

        var skill = SkillService.Find(a.Value.Nome);
        if (skill == null) return NaoEncontrada(a.Value.Nome);

        string comando = autorizado.Command ?? "";

        if (comando.StartsWith(PrefixoDoManual, StringComparison.Ordinal))
        {
            // Autorizado ler. Se agora há script, ler não é mais o que a chamada faria.
            return SoManual(skill) ? Manual(skill) : Mudou;
        }

        // Autorizado um script. Virar manual é inofensivo (só entrega texto); trocar de script
        // ou de linha não é — o usuário aprovou OUTRA coisa.
        if (SoManual(skill)) return Manual(skill);

        if (!string.Equals(comando, ComandoDoScript(skill, a.Value.Extra), StringComparison.Ordinal))
            return Mudou;

        // O MESMO objeto que acabou de ser conferido: reler do disco aqui reabriria a janela.
        return await RodarAsync(skill, a.Value.Extra);
    }

    /// <summary>O texto do SKILL.md, que é tudo o que uma skill de documentação faz.</summary>
    private static string Manual(LocalSkill skill) =>
        skill.Instructions.Length > 0
            ? skill.Instructions
            : $"A habilidade '{skill.Name}' não tem script nem instruções.";

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

        // O Validar já recusou o nome que não existe. Fica aqui para a habilidade que foi
        // apagada entre o pré-voo e a execução — o card pode ter ficado aberto por horas.
        var skill = SkillService.Find(nome);
        if (skill == null) return NaoEncontrada(nome);

        // Skill de documentação: não roda, ENTREGA o texto. É o caso de uma skill que ensina um
        // procedimento em vez de automatizá-lo.
        if (SoManual(skill)) return Manual(skill);

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

            // O MESMO Montar do shell, com o mesmo teto. A skill montava a saída sozinha e não
            // olhava o código de saída: um script que terminava com "exit 1" e escrevia algo
            // voltava sem "ERRO" na frente, e tudo o que decide "falhou" — chip, memória, bloqueio
            // de repetição — via sucesso. Um PowerShell com erro que não encerra (código 0, erro
            // no CLIXML) também passa a contar, como no shell.
            string texto = RunCommandTool.Montar(await saida, await erro, processo.ExitCode);
            bool falhou = Memory.ArtifactExtractor.Falhou(texto);

            if (texto == RunCommandTool.SucessoSemSaida)
                texto = $"Habilidade '{skill.Name}' executada (sem saída).";

            // O corpo do SKILL.md so vai ao modelo QUANDO A CHAMADA FALHA, e e para isso
            // que ele serve: o prompt de sistema lista nome e descricao, nada sobre os
            // argumentos. Sem isto o modelo tinha de adivinhar a assinatura, errava, e
            // recebia de volta um erro sem nenhuma pista da forma certa - foram tres
            // tentativas cegas seguidas. Mandar as instrucoes sempre custaria contexto em
            // toda chamada bem-sucedida; manda-las no erro custa so quando servem.
            if (!falhou)
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

            return texto;
        }
        catch (Exception ex)
        {
            return $"ERRO ao executar a habilidade '{skill.Name}': {ex.Message}";
        }
    }
}
