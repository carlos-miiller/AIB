using System.Threading.Tasks;
using OpenAI.Chat;

namespace AIB.Services;

/// <summary>
/// Contrato base para todas as ferramentas do agente AIB.
/// Toda ferramenta nativa C# implementa esta interface para ser registrada e executada de
/// forma uniforme. Scripts locais (Python/PowerShell) não a implementam: são skills, e chegam
/// ao modelo pela ferramenta <c>skill</c> (ver <see cref="Tools.ExecuteSkillTool"/>).
/// </summary>
public interface ITool
{
    /// <summary>Nome único da ferramenta (ex: "read", "shell"). Ver <see cref="Ferramentas"/>.</summary>
    string Name { get; }

    /// <summary>Descrição clara da finalidade da ferramenta para o LLM.</summary>
    string Description { get; }

    /// <summary>
    /// Definição nativa do schema da ferramenta para a OpenAI SDK.
    /// Este objeto é enviado ao LLM para que ele saiba como e quando chamar a ferramenta.
    /// </summary>
    ChatTool ChatToolDefinition { get; }

    /// <summary>Nível mínimo requerido para o usuário ter acesso à ferramenta.</summary>
    int RequiredLevel { get; }

    /// <summary>
    /// Executa a ferramenta com os argumentos fornecidos pelo LLM.
    /// Sempre retorna uma string de resultado (sucesso ou erro) para o agente.
    /// </summary>
    /// <param name="argumentsJson">JSON com os argumentos, conforme o schema definido em ChatToolDefinition.</param>
    /// <param name="userLevel">
    /// Nível atual do usuário. O registry já barrou quem está abaixo de <see cref="RequiredLevel"/>;
    /// hoje nenhuma ferramenta o usa na execução.
    /// </param>
    Task<string> ExecuteAsync(string argumentsJson, int userLevel = 1);

    /// <summary>
    /// Executa sabendo O QUE foi autorizado: o contexto que o card mostrou, ou que a dispensa
    /// descreveu. Null para ferramenta que não pede confirmação. É o único ponto de execução que
    /// o registry usa.
    /// <para>
    /// Existe porque entre autorizar e executar há um intervalo — segundos na dispensa, horas
    /// com o card aberto — e o disco pode mudar nele. Uma skill de documentação era dispensada
    /// como "só lê o manual"; se o SKILL.md ganhasse um script antes da execução, o script
    /// rodava sem card nenhum. Quem sabe comparar o autorizado com o que vai acontecer agora é
    /// a ferramenta. Default: executa como sempre.
    /// </para>
    /// </summary>
    Task<string> ExecutarAutorizadoAsync(string argumentsJson, int userLevel, CommandConfirmationContext? autorizado)
        => ExecuteAsync(argumentsJson, userLevel);

    /// <summary>
    /// Se a execução precisa de autorização humana explícita antes de rodar.
    /// Default falso: só ferramentas que alteram a máquina do usuário sobrescrevem.
    /// </summary>
    bool RequiresConfirmation => false;

    /// <summary>
    /// Se ESTA chamada passa pelo portão. Default: <see cref="RequiresConfirmation"/>, a mesma
    /// resposta para toda chamada.
    /// <para>
    /// Existe para a ferramenta que mistura ler e agir. No navegador, ver, procurar, rolar e ler
    /// a tabela não mudam nada e não perguntam; clicar e digitar perguntam. Quem responde false
    /// aqui tem de recusar na execução se receber sem autorização algo que pediria cartão.
    /// </para>
    /// </summary>
    bool PedeConfirmacao(string argumentsJson) => RequiresConfirmation;

    /// <summary>
    /// Confere os argumentos ANTES de qualquer coisa acontecer. Devolver texto RECUSA a
    /// execução, e o texto vai ao modelo como resultado.
    /// <para>
    /// Roda antes do portão de confirmação, de propósito: uma chamada que não pode dar certo
    /// não deve virar pergunta para o usuário. Visto em produção — o modelo pediu uma planilha
    /// num caminho inexistente quatro vezes, e cada uma abriu um modal pedindo autorização para
    /// executar algo que ia falhar de qualquer jeito.
    /// </para>
    /// <para>
    /// Default null: quem não tem o que conferir não confere nada, e o comportamento é o de
    /// antes. Recusar aqui é sempre seguro — é um "não".
    /// </para>
    /// </summary>
    string? Validar(string argumentsJson) => null;

    /// <summary>
    /// Se ESTA chamada pode correr sem o portão humano, porque o alvo dela está numa pasta que o
    /// usuário marcou como de confiança. Ver <see cref="PastasSemConfirmacao"/>.
    /// <para>
    /// Default falso: dispensar é sempre uma escolha escrita da ferramenta, e quem não sabe
    /// responder pergunta. Quem decide de verdade é o registry, que ainda checa o contexto da
    /// conversa e a floor list antes de aceitar a dispensa.
    /// </para>
    /// <para>
    /// O outro caso é a skill de documentação (<c>markdown</c>): ela só entrega o manual ao
    /// modelo e não executa nada. Ver <see cref="Tools.ExecuteSkillTool.DispensaConfirmacao"/>.
    /// </para>
    /// </summary>
    bool DispensaConfirmacao(string argumentsJson) => false;

    /// <summary>
    /// Se o <see cref="CommandConfirmationContext.Command"/> desta ferramenta é uma linha de
    /// comando que a <see cref="CommandFloorList"/> sabe ler.
    /// <para>
    /// Default falso: a floor list foi escrita para shell. No <c>write</c> e no <c>edit</c> o
    /// Command é "CRIAR/SOBRESCREVER/EDITAR &lt;caminho&gt;", e um caminho com "logoff",
    /// "shutdown" ou "format" no nome era recusado como "desligamento/reboot" — uma recusa que
    /// mente sobre o motivo manda o modelo procurar solução no lugar errado. Só o
    /// <c>shell</c> e a <c>skill</c> (que monta a linha de comando do script) respondem true.
    /// </para>
    /// </summary>
    bool PassaPelaFloorList => false;

    /// <summary>
    /// Se a chamada só vale quando o pedido só pode ter vindo do usuário: com texto de terceiros
    /// no contexto (e-mail, página), o registry recusa. Ver <see cref="Tools.LembrarTool"/>.
    /// </summary>
    bool SoComFalaDoUsuario => false;

    /// <summary>
    /// A mesma pergunta, para uma operação já descrita. É o que o registry consulta.
    /// <para>
    /// Existe porque uma ferramenta pode ter operações dos dois tipos. A <c>skill</c> que só
    /// entrega o manual autoriza "LER MANUAL &lt;pasta&gt;\SKILL.md" — um caminho, não uma linha de
    /// comando —, e uma pasta de skill com "shutdown" no nome era barrada como desligamento.
    /// </para>
    /// </summary>
    bool PassaPelaFloorListCom(CommandConfirmationContext contexto) => PassaPelaFloorList;

    /// <summary>
    /// A floor list pela OPERAÇÃO, para ferramenta que sabe o que vai fazer sem precisar de regex
    /// sobre uma linha de comando. Devolve o motivo da recusa, ou <c>null</c>.
    /// <para>
    /// Existe para a ferramenta tipada não virar o caminho por baixo do piso: o shell barra
    /// <c>Remove-Item -Recurse</c> abaixo do Nível 7 pela regex, e uma ferramenta que apaga pasta
    /// sem passar por regra nenhuma faria o mesmo no nível 2. O registry chama isto nos dois
    /// caminhos (com e sem dispensa), respeitando <c>ConfirmDangerousCommands</c>.
    /// </para>
    /// </summary>
    string? PisoTipado(CommandConfirmationContext contexto, int userLevel) => null;

    /// <summary>
    /// Monta o que o cartão de confirmação mostra ao usuário. Fica na ferramenta porque só ela sabe interpretar
    /// o próprio JSON de argumentos — o registry não pode adivinhar qual campo é "o comando".
    /// Devolver null recusa a execução: se a ferramenta pede confirmação e não consegue
    /// descrever o que vai fazer, o usuário não tem como autorizar com conhecimento de causa.
    /// </summary>
    CommandConfirmationContext? BuildConfirmationContext(string argumentsJson, int userLevel) => null;
}
