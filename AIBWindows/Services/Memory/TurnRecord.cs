using System.Collections.Generic;

namespace AIB.Services.Memory;

/// <summary>Uma tool_call gravada, com os argumentos exatamente como o modelo os emitiu.</summary>
public sealed record ToolCallRecord(string Id, string Name, string Arguments);

/// <summary>
/// Uma mensagem em forma serializável. O <c>ChatMessage</c> do SDK não tem formato estável em
/// disco — muda com a versão do pacote — e <c>raw.jsonl</c> precisa continuar legível daqui a
/// muitas atualizações. Este DTO é o contrato de disco.
/// </summary>
/// <remarks>
/// Os campos depois de <c>ToolCallId</c> são a conta de cada passo, e ficam nulos — fora do
/// arquivo — quando não se aplicam ou quando a mensagem é de antes de existirem.
/// </remarks>
/// <param name="AtUtc">Quando a mensagem entrou na conversa.</param>
/// <param name="Modelo">Assistente: o modelo que falou.</param>
/// <param name="TokensEntrada">Assistente: o prompt avaliado, como o provider relatou.</param>
/// <param name="TokensSaida">Assistente: o que o modelo gerou, como o provider relatou.</param>
/// <param name="DuracaoMs">
/// Assistente: do envio do prompt ao fim do stream. Ferramenta: da chamada ao resultado,
/// incluindo a espera pela sua decisão.
/// </param>
/// <param name="EsperaHumanaMs">Ferramenta: a parte da duração parada no cartão de confirmação.</param>
/// <param name="Decisao">Ferramenta: como passou pelo portão — ver <c>ToolRegistry.ExecuteToolAsync</c>.</param>
/// <param name="Falhou">Ferramenta: se o resultado é um erro ou uma recusa.</param>
public sealed record MessageRecord(
    string Role,
    string Text,
    IReadOnlyList<ToolCallRecord>? ToolCalls = null,
    string? ToolCallId = null,
    string? AtUtc = null,
    string? Modelo = null,
    int? TokensEntrada = null,
    int? TokensSaida = null,
    long? DuracaoMs = null,
    long? EsperaHumanaMs = null,
    string? Decisao = null,
    bool? Falhou = null);

/// <summary>
/// O que se sabe de uma mensagem além do texto dela. Mora fora da <c>ChatMessage</c> do SDK,
/// que não tem onde guardar isto, e vira os campos opcionais de <see cref="MessageRecord"/>.
/// </summary>
public sealed record MetaDaMensagem(
    string? AtUtc = null,
    string? Modelo = null,
    int? TokensEntrada = null,
    int? TokensSaida = null,
    long? DuracaoMs = null,
    long? EsperaHumanaMs = null,
    string? Decisao = null,
    bool? Falhou = null);

/// <summary>
/// Um turno completo gravado em <c>raw.jsonl</c>: uma linha por turno.
/// <para>
/// Guarda as mensagens E os artefatos já extraídos. Os artefatos são deriváveis das mensagens,
/// mas gravá-los congela o resultado da extração da época — se o extrator mudar amanhã, dá
/// para comparar o que ele via antes com o que passou a ver.
/// </para>
/// </summary>
/// <param name="Id">
/// Identidade do turno. Nula nos gravados antes de existir. Serve à recuperação de um turno
/// interrompido: é por ela que se sabe se ele já tinha chegado ao arquivo.
/// </param>
public sealed record TurnRecord(
    int Index,
    string AtUtc,
    IReadOnlyList<MessageRecord> Messages,
    IReadOnlyList<Artifact> Artifacts,
    string? Id = null);
