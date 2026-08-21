using System.Collections.Generic;

namespace AIB.Services.Memory;

/// <summary>Uma tool_call gravada, com os argumentos exatamente como o modelo os emitiu.</summary>
public sealed record ToolCallRecord(string Id, string Name, string Arguments);

/// <summary>
/// Uma mensagem em forma serializável. O <c>ChatMessage</c> do SDK não tem formato estável em
/// disco — muda com a versão do pacote — e <c>raw.jsonl</c> precisa continuar legível daqui a
/// muitas atualizações. Este DTO é o contrato de disco.
/// </summary>
public sealed record MessageRecord(
    string Role,
    string Text,
    IReadOnlyList<ToolCallRecord>? ToolCalls = null,
    string? ToolCallId = null);

/// <summary>
/// Um turno completo gravado em <c>raw.jsonl</c>: uma linha por turno.
/// <para>
/// Guarda as mensagens E os artefatos já extraídos. Os artefatos são deriváveis das mensagens,
/// mas gravá-los congela o resultado da extração da época — se o extrator mudar amanhã, dá
/// para comparar o que ele via antes com o que passou a ver.
/// </para>
/// </summary>
public sealed record TurnRecord(
    int Index,
    string AtUtc,
    IReadOnlyList<MessageRecord> Messages,
    IReadOnlyList<Artifact> Artifacts);
