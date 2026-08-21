using System.Collections.Generic;
using AIB.Services.Ai;
using OpenAI.Chat;

namespace AIB.Services.Agent;

/// <summary>Como o turno terminou. Visível para o chamador — nunca um sucesso silencioso.</summary>
public enum TurnOutcome
{
    /// <summary>O modelo entregou uma resposta final.</summary>
    Answered = 0,

    /// <summary>O teto de iterações ReAct estourou com o modelo ainda pedindo ferramentas.</summary>
    IterationLimitReached = 1,

    /// <summary>O stream terminou sem texto e sem tool call (modelo devolveu vazio).</summary>
    EmptyResponse = 2
}

/// <summary>Evento do turno. A ConversationService traduz para o que a UI consome.</summary>
public abstract record AgentEvent
{
    private AgentEvent() { }

    /// <summary>Texto de canal final — vai para o balão do usuário.</summary>
    public sealed record Text(string Value) : AgentEvent;

    /// <summary>Conteúdo técnico: think, logs de ferramenta, diagnóstico do stream.</summary>
    public sealed record Technical(string Value) : AgentEvent;

    /// <summary>Contador de tokens durante o stream.</summary>
    public sealed record TokenUsage(int Total, int Max, int? Cached) : AgentEvent;

    /// <summary>
    /// Fecha um segmento do turno: a iteração ReAct terminou pedindo ferramenta, e o texto que
    /// veio até aqui é uma fala completa do agente.
    /// <para>
    /// Existe porque o laço ReAct é o único lugar que sabe onde uma fala acaba e a próxima
    /// começa — sem este evento a UI só enxerga um fluxo contínuo de texto e não tem como
    /// separar "vou ler o arquivo" de "encontrei o erro na linha 12".
    /// </para>
    /// </summary>
    public sealed record TurnSegment(int Iteration) : AgentEvent;

    /// <summary>Fim do turno. Emitido exatamente uma vez, por último.</summary>
    public sealed record Completed(TurnOutcome Outcome, int IterationsUsed) : AgentEvent;
}

/// <summary>Parâmetros de um turno. O AgentLoop não guarda estado entre turnos.</summary>
public sealed record AgentTurnRequest(
    IMessageStore Store,
    IReadOnlyList<ChatTool> Tools,
    int UserLevel,
    ChatRequestOptions Options,
    bool VerboseLogging);
