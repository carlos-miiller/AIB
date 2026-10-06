using System.Collections.Generic;
using OpenAI.Chat;

namespace AIB.Services.Ai;

/// <summary>Uma tool call reconstruída a partir de texto que o modelo escreveu como prosa.</summary>
/// <param name="ToolName">Nome da ferramenta, já validado contra as tools ativas.</param>
/// <param name="ArgumentsJson">JSON de argumentos, SEMPRE produzido por JsonSerializer.</param>
/// <param name="MatchedText">Trecho que casou — só para log técnico.</param>
public sealed record HealedToolCall(string ToolName, string ArgumentsJson, string MatchedText);

/// <summary>
/// Cura a alucinação de modelos pequenos que escrevem a chamada de ferramenta como texto
/// em vez de usar function calling. Recurso INTENCIONAL (documentação/tecnica/02-turno-e-provedores.md §11).
/// </summary>
public interface IToolCallHealer
{
    /// <summary>
    /// Tenta extrair uma tool call de <paramref name="finalChannelText"/>.
    /// CONTRATO: só recebe texto já classificado como canal FINAL — nunca conteúdo de
    /// &lt;think&gt;. Retorna false se não casar, se a ferramenta não existir em
    /// <paramref name="activeTools"/>, ou se os argumentos não puderem ser mapeados
    /// para o schema real da ferramenta.
    /// </summary>
    bool TryHeal(
        string finalChannelText,
        IReadOnlyList<ChatTool> activeTools,
        out HealedToolCall healed);
}
