using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using AIB.Services.Tools;
using OpenAI.Chat;

namespace AIB.Services.Memory;

/// <summary>
/// Extrai os fatos literais de um turno. CÓDIGO PURO — nunca chama modelo.
/// <para>
/// Determinístico e testável sem rede: é a diferença entre um artefato em que se pode agir e
/// uma paráfrase plausível. O resumo narrativo do capítulo pode errar detalhe sem consequência;
/// o caminho gravado aqui vai voltar para as mãos de um agente que executa comandos.
/// </para>
/// </summary>
public static class ArtifactExtractor
{
    /// <summary>Texto exato que o portão devolve quando o usuário recusa.</summary>
    private const string TextoRecusa = "Ação Rejeitada pelo Usuário.";

    public static IReadOnlyList<Artifact> Extract(Turn turn) =>
        Extract(turn?.Messages);

    /// <summary>
    /// Percorre o turno pareando <c>assistant(tool_calls)</c> com <c>tool(result)</c> por id.
    /// Chamada sem resultado é ignorada: sem o resultado não se sabe se algo aconteceu, e
    /// registrar intenção como fato é pior que não registrar nada.
    /// </summary>
    public static IReadOnlyList<Artifact> Extract(IReadOnlyList<ChatMessage>? messages)
    {
        var artefatos = new List<Artifact>();
        if (messages == null || messages.Count == 0) return artefatos;

        // id da tool_call -> (nome, argumentos crus). Não é dicionário de saída: a ORDEM dos
        // artefatos é a ordem em que os resultados chegaram, que é a ordem de execução.
        var pendentes = new Dictionary<string, (string Nome, string Args)>(StringComparer.Ordinal);

        foreach (var msg in messages)
        {
            if (msg is AssistantChatMessage assistant && assistant.ToolCalls is { Count: > 0 })
            {
                foreach (var call in assistant.ToolCalls)
                {
                    if (call?.Id == null) continue;
                    pendentes[call.Id] = (call.FunctionName ?? "", call.FunctionArguments?.ToString() ?? "");
                }
                continue;
            }

            if (msg is not ToolChatMessage tool || tool.ToolCallId == null) continue;
            if (!pendentes.TryGetValue(tool.ToolCallId, out var chamada)) continue;

            pendentes.Remove(tool.ToolCallId);

            var artefato = Build(chamada.Nome, chamada.Args, Turn.TextOf(tool));
            if (artefato != null) artefatos.Add(artefato);
        }

        return artefatos;
    }

    private static Artifact? Build(string ferramenta, string argumentosJson, string resultado)
    {
        bool recusado = resultado.Contains(TextoRecusa, StringComparison.Ordinal);
        bool falhou = recusado || resultado.StartsWith("ERRO", StringComparison.OrdinalIgnoreCase);

        switch (ferramenta)
        {
            case "write_file":
            {
                string caminho = CaminhoDe(argumentosJson);
                if (caminho.Length == 0) return null;
                if (recusado) return new Artifact(ArtifactKind.Denied, ferramenta, caminho, true, "gravação recusada");

                string? detalhe = TamanhoDoConteudo(argumentosJson);
                return new Artifact(ArtifactKind.FileWritten, ferramenta, caminho, falhou,
                    falhou ? PrimeiraLinha(resultado) : detalhe);
            }

            case "read_file":
            {
                string caminho = CaminhoDe(argumentosJson);
                if (caminho.Length == 0) return null;
                if (recusado) return new Artifact(ArtifactKind.Denied, ferramenta, caminho, true, "leitura recusada");

                return new Artifact(ArtifactKind.FileRead, ferramenta, caminho, falhou,
                    falhou ? PrimeiraLinha(resultado) : null);
            }

            case "run_command":
            {
                string comando = StringDe(argumentosJson, "command");
                if (comando.Length == 0) return null;
                if (recusado) return new Artifact(ArtifactKind.Denied, ferramenta, comando, true, "comando recusado");

                // O erro literal é o que importa quando falha: "acesso negado" e "arquivo não
                // encontrado" pedem correções opostas, e um resumo apaga a diferença.
                return new Artifact(ArtifactKind.CommandRun, ferramenta, comando, falhou,
                    falhou ? PrimeiraLinha(resultado) : null);
            }

            default:
                // Ferramenta sem extrator próprio: registra só a recusa, que vale para qualquer
                // uma. Sucesso genérico não tem literal a preservar.
                return recusado
                    ? new Artifact(ArtifactKind.Denied, ferramenta, ferramenta, true, "chamada recusada")
                    : null;
        }
    }

    /// <summary>
    /// Caminho ABSOLUTO resolvido. O modelo escreve caminho relativo com frequência, e
    /// "config.json" guardado como está deixa de ser um literal — vira ambiguidade.
    /// Passa pelo mesmo reparo de escape das ferramentas.
    /// </summary>
    private static string CaminhoDe(string argumentosJson)
    {
        string bruto = PathArgumentRepair.Normalize(StringDe(argumentosJson, "path"));
        if (string.IsNullOrWhiteSpace(bruto)) return "";

        try { return Path.GetFullPath(bruto); }
        catch { return bruto; }
    }

    private static string? TamanhoDoConteudo(string argumentosJson)
    {
        string conteudo = StringDe(argumentosJson, "content");
        return conteudo.Length == 0 ? null : $"{conteudo.Length} caracteres";
    }

    private static string StringDe(string argumentosJson, string propriedade)
    {
        if (string.IsNullOrWhiteSpace(argumentosJson)) return "";
        try
        {
            var raiz = JsonSerializer.Deserialize<JsonElement>(argumentosJson);
            if (raiz.ValueKind != JsonValueKind.Object) return "";
            return raiz.TryGetProperty(propriedade, out var el) && el.ValueKind == JsonValueKind.String
                ? el.GetString() ?? ""
                : "";
        }
        catch (JsonException)
        {
            // Argumento malformado é comum com modelo pequeno. Não é motivo para derrubar a
            // extração do turno inteiro.
            return "";
        }
    }

    private static string PrimeiraLinha(string texto)
    {
        if (string.IsNullOrEmpty(texto)) return "";
        int quebra = texto.IndexOf('\n');
        string linha = quebra < 0 ? texto : texto[..quebra];
        linha = linha.Trim();
        return linha.Length > 200 ? linha[..200] + "…" : linha;
    }
}
