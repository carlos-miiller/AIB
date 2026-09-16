namespace AIB.Services.Memory;

/// <summary>Natureza de um artefato literal extraído de um turno.</summary>
public enum ArtifactKind
{
    /// <summary>Arquivo gravado por write.</summary>
    FileWritten,

    /// <summary>Arquivo lido por read.</summary>
    FileRead,

    /// <summary>Comando executado por shell.</summary>
    CommandRun,

    /// <summary>Chamada barrada no portão de confirmação — o usuário disse não.</summary>
    Denied
}

/// <summary>
/// Fato literal de um turno: caminho absoluto, comando exato, mensagem de erro.
/// <para>
/// É a metade do capítulo que NUNCA passa pelo modelo. Resumir
/// <c>C:\Users\Carlo\CPAPS\AIB\AIBWindows\Services\Ai\OllamaProvider.cs</c> como "um arquivo do
/// provider" destrói exatamente a informação que tem valor: o literal. Num jogo de RP o
/// resumo aproximado gera inconsistência de enredo; aqui gera agente agindo sobre caminho
/// errado, com shell na mão.
/// </para>
/// </summary>
/// <param name="Kind">Natureza do artefato.</param>
/// <param name="Tool">Ferramenta que o produziu.</param>
/// <param name="Value">O literal: caminho absoluto resolvido, ou a linha de comando exata.</param>
/// <param name="Failed">Se a execução falhou. Fracasso é informação tão útil quanto sucesso.</param>
/// <param name="Detail">Complemento curto: tamanho gravado, ou a mensagem de erro literal.</param>
public sealed record Artifact(
    ArtifactKind Kind,
    string Tool,
    string Value,
    bool Failed,
    string? Detail = null)
{
    /// <summary>Uma linha para o bloco ARTEFATOS do capítulo.</summary>
    public string Render()
    {
        // Comando que apagou algo não sai como linha pronta para copiar: o modelo já repetiu
        // um Remove-Item -Recurse tirado daqui. Ver ComandoQueApaga. O literal continua no
        // registro (Value, raw.jsonl); só a forma no prompt muda.
        if (Kind == ArtifactKind.CommandRun && ComandoQueApaga.Eh(Value))
        {
            string fato = "- " + ComandoQueApaga.Descrever(Value);
            if (Failed) fato += " [FALHOU]";
            return fato;
        }

        string prefixo = Kind switch
        {
            ArtifactKind.FileWritten => "gravou",
            ArtifactKind.FileRead => "leu",
            ArtifactKind.CommandRun => "executou",
            ArtifactKind.Denied => "negado pelo usuário:",
            _ => Tool
        };

        string linha = $"- {prefixo} {Value}";
        if (Failed && Kind != ArtifactKind.Denied) linha += " [FALHOU]";
        if (!string.IsNullOrEmpty(Detail)) linha += $" ({Detail})";
        return linha;
    }
}
