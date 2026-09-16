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

    /// <summary>Conteúdo técnico: logs de ferramenta, diagnóstico do stream.</summary>
    public sealed record Technical(string Value) : AgentEvent;

    /// <summary>
    /// Texto do canal de raciocínio — o que o modelo pensa antes de responder.
    /// <para>
    /// Separado do <see cref="Technical"/> porque tem um significado que os logs de ferramenta
    /// não têm: o modelo já está GERANDO. É o primeiro sinal de vida de um turno, e chega bem
    /// antes da primeira palavra da resposta — em modelo de raciocínio rodando em CPU, dezenas
    /// de segundos antes. Quem desenha a tela precisa distinguir isso de uma linha de log.
    /// </para>
    /// <para>
    /// O conteúdo continua indo para o console junto com o resto do técnico; o que muda é que
    /// agora dá para reagir à CHEGADA dele.
    /// </para>
    /// </summary>
    public sealed record Reasoning(string Value) : AgentEvent;

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

    /// <summary>
    /// Uma ferramenta começou a executar.
    /// <para>
    /// Viaja tipado, e não como texto técnico, porque a interface precisa dele para desenhar a
    /// cadeia de ações. Antes a tela lia o nome da ferramenta com uma expressão regular sobre a
    /// mesma string de log que o console imprime — qualquer ajuste na frase do log quebrava a
    /// exibição sem quebrar teste nenhum.
    /// </para>
    /// <para>
    /// Quando o modelo pede várias ferramentas na mesma iteração, TODAS são anunciadas antes de
    /// a primeira executar: elas rodam em paralelo, e a cadeia precisa mostrar isso.
    /// </para>
    /// </summary>
    public sealed record ToolStarted(string Id, string Tool, string Arguments) : AgentEvent;

    /// <summary>
    /// Uma ferramenta terminou. <paramref name="Artifact"/> traz o literal já extraído — o
    /// caminho absoluto, a linha de comando — pelo mesmo extrator que alimenta a memória.
    /// </summary>
    /// <param name="Arguments">
    /// Os argumentos crus da chamada. A tela precisa deles para descrever ferramentas que não
    /// deixam artefato — sem eles, um <c>glob</c> aparecia no registro só como "glob".
    /// </param>
    /// <param name="DuracaoMs">Da chamada ao resultado, incluindo a espera pela sua decisão.</param>
    /// <param name="EsperaHumanaMs">A parte de <paramref name="DuracaoMs"/> parada no cartão de confirmação.</param>
    /// <param name="Decisao">
    /// Como a chamada passou (ou não) pelo portão: <c>automatica</c>, <c>permitida</c>,
    /// <c>recusada</c>, <c>sempre_na_sessao</c>… Ver <see cref="ToolRegistry.ExecuteToolAsync"/>.
    /// </param>
    public sealed record ToolFinished(
        string Id,
        string Tool,
        bool Failed,
        Memory.Artifact? Artifact,
        string Result,
        string Arguments = "",
        long DuracaoMs = 0,
        long EsperaHumanaMs = 0,
        string? Decisao = null) : AgentEvent;

    /// <summary>
    /// Uma volta ao modelo terminou, e a fala dela vai ao histórico em seguida. Emitido ANTES da
    /// escrita, para quem registra o turno poder anotar a mensagem com o custo dela.
    /// </summary>
    /// <param name="TokensEntrada">O prompt avaliado, como o provider relatou. Nulo se não relatou.</param>
    /// <param name="TokensSaida">O que o modelo gerou, como o provider relatou. Nulo se não relatou.</param>
    /// <param name="DuracaoMs">Do envio do prompt ao fim do stream.</param>
    /// <param name="CustoUsd">O que esta volta custou, quando o provedor cobra e relata.</param>
    public sealed record ModelReplied(
        string Modelo,
        int? TokensEntrada,
        int? TokensSaida,
        long DuracaoMs,
        decimal? CustoUsd = null) : AgentEvent;

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
