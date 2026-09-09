using System.Threading.Tasks;
using OpenAI.Chat;

namespace AIB.Services;

/// <summary>
/// Contrato base para todas as ferramentas do agente AIB.
/// Toda ferramenta, seja nativa C# ou um script local Python/PowerShell,
/// implementa esta interface para ser registrada e executada de forma uniforme.
/// </summary>
public interface ITool
{
    /// <summary>Nome único da ferramenta (ex: "remember", "ocr_screen").</summary>
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
    /// <param name="userLevel">Nível atual do usuário para restrições avançadas de sandbox.</param>
    Task<string> ExecuteAsync(string argumentsJson, int userLevel = 1);

    /// <summary>
    /// Se a execução precisa de autorização humana explícita antes de rodar.
    /// Default falso: só ferramentas que alteram a máquina do usuário sobrescrevem.
    /// </summary>
    bool RequiresConfirmation => false;

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
    /// Monta o que o modal mostra ao usuário. Fica na ferramenta porque só ela sabe interpretar
    /// o próprio JSON de argumentos — o registry não pode adivinhar qual campo é "o comando".
    /// Devolver null recusa a execução: se a ferramenta pede confirmação e não consegue
    /// descrever o que vai fazer, o usuário não tem como autorizar com conhecimento de causa.
    /// </summary>
    CommandConfirmationContext? BuildConfirmationContext(string argumentsJson, int userLevel) => null;
}
