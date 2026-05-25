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
}
