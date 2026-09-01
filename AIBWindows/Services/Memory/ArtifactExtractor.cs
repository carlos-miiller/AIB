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

    /// <summary>
    /// Um artefato a partir de uma chamada já resolvida, sem precisar do turno inteiro.
    /// <para>
    /// É o mesmo caminho que a memória usa no fim do turno, exposto para a interface poder
    /// mostrar o literal AO VIVO, enquanto a cadeia de ações acontece. Ter dois extratores —
    /// um para a tela, outro para o disco — deixaria a bolha e o capítulo discordando sobre o
    /// que foi feito.
    /// </para>
    /// </summary>
    public static Artifact? Construir(string ferramenta, string argumentosJson, string resultado) =>
        Build(ferramenta, argumentosJson, resultado);

    /// <summary>
    /// Se o resultado de uma ferramenta representa fracasso.
    /// <para>
    /// Separado do <see cref="Construir"/> porque nem toda ferramenta tem extrator próprio: a
    /// que não tem devolve artefato nulo mesmo quando falhou, e deduzir o fracasso da ausência
    /// de artefato marcaria todo erro dessas como sucesso.
    /// </para>
    /// </summary>
    public static bool Falhou(string? resultado)
    {
        if (string.IsNullOrEmpty(resultado)) return false;

        return resultado.Contains(TextoRecusa, StringComparison.Ordinal)
            || resultado.StartsWith("ERRO", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Se o resultado veio de uma recusa do usuário no portão de confirmação.</summary>
    public static bool Recusado(string? resultado) =>
        resultado != null && resultado.Contains(TextoRecusa, StringComparison.Ordinal);

    /// <summary>
    /// Resumo curto dos argumentos, para o chip em execução (§4.2d da spec de chat).
    /// <para>
    /// Sai dos mesmos campos que viram artefato: caminho para as ferramentas de arquivo, linha
    /// de comando para <c>run_command</c>. Ferramenta sem extrator devolve vazio — melhor um
    /// chip só com o nome do que um JSON cru espremido em 11px.
    /// </para>
    /// </summary>
    public static string ResumirArgumento(string ferramenta, string argumentosJson) =>
        ferramenta switch
        {
            "write_file" or "read_file" => CaminhoDe(argumentosJson),
            "run_command" => StringDe(argumentosJson, "command"),
            "execute_skill" => ChamadaDeSkill(argumentosJson),
            _ => ""
        };

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

            case "execute_skill":
            {
                // O literal aqui e a chamada: nome da habilidade mais os argumentos que ela
                // recebeu. So o nome da ferramenta nao serve — "execute_skill" repetido na
                // trilha nao diz QUAL habilidade rodou, que e a unica coisa que se quer saber
                // ao olhar para tras.
                string chamada = ChamadaDeSkill(argumentosJson);
                if (chamada.Length == 0) return null;
                if (recusado) return new Artifact(ArtifactKind.Denied, ferramenta, chamada, true, "habilidade recusada");

                return new Artifact(ArtifactKind.CommandRun, ferramenta, chamada, falhou,
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
    /// <summary>
    /// "ler-planilha -Path C:\lista.xlsx" — nome da habilidade e os argumentos dela.
    /// Vazio quando nem o nome veio, que e o unico caso em que nada aconteceu.
    /// </summary>
    private static string ChamadaDeSkill(string argumentosJson)
    {
        string nome = StringDe(argumentosJson, "skill_name").Trim();
        if (nome.Length == 0) return "";

        string extra = StringDe(argumentosJson, "arguments").Trim();
        return extra.Length == 0 ? nome : nome + " " + extra;
    }

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
