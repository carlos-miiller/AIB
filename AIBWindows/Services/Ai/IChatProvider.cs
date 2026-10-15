using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using OpenAI.Chat;

namespace AIB.Services.Ai;

/// <summary>
/// Abstração de um provedor de chat (Ollama, OpenRouter). Tudo que sabe falar HTTP,
/// montar payload e classificar o stream vive atrás desta interface.
/// </summary>
public interface IChatProvider
{
    /// <summary>Nome do provider para log/diagnóstico ("Ollama", "OpenRouter").</summary>
    string Name { get; }

    /// <summary>Modelo efetivamente configurado neste provider.</summary>
    string Model { get; }

    /// <summary>
    /// Streaming de uma única resposta do modelo. NUNCA muta <paramref name="messages"/>.
    /// Deve ser 100% assíncrono: nenhuma leitura bloqueante de socket.
    /// <paramref name="tools"/> nunca é null; passe uma lista vazia para desligar ferramentas.
    /// Quando a lista não é vazia, as definições vão para a requisição em TODOS os caminhos.
    /// </summary>
    IAsyncEnumerable<StreamChunk> StreamAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ChatTool> tools,
        ChatRequestOptions options,
        CancellationToken ct);

    /// <summary>Chamada não-streaming. Usada pelo warmup e por AskStatelessAsync.</summary>
    Task<ChatCompletionResult> CompleteAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ChatTool> tools,
        ChatRequestOptions options,
        CancellationToken ct);

    /// <summary>
    /// Prefill de carregamento do modelo. No Ollama: POST /api/generate com o keep-alive em vigor
    /// (<see cref="ChatRequestOptions.KeepAliveAtual"/>).
    /// No OpenRouter: no-op. Falha de rede vira log e retorno normal — aquecimento é best-effort.
    /// A única exceção que sobe é OperationCanceledException: engolir o cancelamento faria o
    /// aquecimento seguir rodando durante o encerramento do app.
    /// </summary>
    Task WarmupAsync(CancellationToken ct);

    /// <summary>
    /// Tokens que o provedor manda JUNTO com <paramref name="messages"/> sem que estejam nelas: o
    /// raciocínio guardado das voltas com ferramenta, que ele devolve ao modelo. Zero onde não há.
    /// <para>
    /// O contador e a compactação contam o histórico; sem isto não viam esse raciocínio. Visto
    /// em 15/10: o OpenRouter cobrou 239.393 tokens de entrada numa volta em que a conta da AIB
    /// dava ~190 mil.
    /// </para>
    /// </summary>
    int RaciocinioReenviado(IReadOnlyList<ChatMessage> messages, System.Func<string, int> contar) => 0;
}
