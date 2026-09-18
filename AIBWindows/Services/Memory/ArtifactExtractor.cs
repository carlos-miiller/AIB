using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
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
    /// <summary>Texto exato que o portão devolve quando o usuário recusa — o mesmo, por referência.</summary>
    private const string TextoRecusa = ToolRegistry.RecusaDoUsuario;

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
    /// A primeira linha não vazia do resultado, com teto de 160 caracteres — a mensagem de erro
    /// que o chip de falha e o registro de ações mostram.
    /// </summary>
    public static string? PrimeiraLinhaDoErro(string? resultado)
    {
        if (string.IsNullOrWhiteSpace(resultado)) return null;

        foreach (var linha in resultado.Split('\n'))
        {
            string limpa = linha.Trim();
            if (limpa.Length > 0) return limpa.Length > 160 ? limpa[..160] + "…" : limpa;
        }

        return null;
    }

    /// <summary>
    /// Resumo curto dos argumentos, para o chip em execução (§4.2d da spec de chat).
    /// <para>
    /// Sai dos mesmos campos que viram artefato: caminho para as ferramentas de arquivo, linha
    /// de comando para <c>shell</c>. Ferramenta sem extrator devolve vazio — melhor um
    /// chip só com o nome do que um JSON cru espremido em 11px.
    /// </para>
    /// </summary>
    public static string ResumirArgumento(string ferramenta, string argumentosJson) =>
        ferramenta switch
        {
            Ferramentas.Gravar or Ferramentas.Ler or Ferramentas.Editar => CaminhoDe(argumentosJson),
            Ferramentas.Shell => StringDe(argumentosJson, "command"),
            Ferramentas.Habilidade => ChamadaDeSkill(argumentosJson),
            Ferramentas.Procurar => BuscaDe(argumentosJson, comFiltro: false),
            Ferramentas.Buscar => BuscaDe(argumentosJson, comFiltro: true),
            Ferramentas.Email => ConsultaDeEmail(argumentosJson),
            Ferramentas.LerEmail => "e-mail desta conversa",
            _ => ""
        };

    /// <summary>
    /// O resultado em poucas palavras, para o registro de ações e o tooltip (§6.4 e):
    /// "12 arquivos", "3 acertos em 2 arquivos", "saída: 4 linhas".
    /// <para>
    /// SÓ PARA A TELA. Sai do texto que a ferramenta devolveu ao modelo, e não entra na memória:
    /// o artefato guarda o literal, e um resumo contado a partir de texto é aproximação.
    /// Formato desconhecido devolve nulo — a linha fica sem resumo, nunca com um inventado.
    /// </para>
    /// </summary>
    public static string? ResumirResultado(string ferramenta, string? resultado)
    {
        if (string.IsNullOrWhiteSpace(resultado) || Falhou(resultado)) return null;

        switch (ferramenta)
        {
            case Ferramentas.Ler:
            {
                var pasta = Regex.Match(resultado, @"é uma PASTA, com (\d+) subpasta\(s\) e (\d+) arquivo\(s\)");
                if (pasta.Success)
                    return $"{Plural(pasta.Groups[2].Value, "arquivo", "arquivos")}, "
                           + Plural(pasta.Groups[1].Value, "pasta", "pastas");

                if (resultado.Contains("é uma pasta, e está vazia", StringComparison.Ordinal))
                    return "pasta vazia";

                int lidas = resultado.Split('\n').Count(l => Regex.IsMatch(l, @"^\s*\d+\t"));
                return lidas == 0 ? null : Plural(lidas.ToString(), "linha lida", "linhas lidas");
            }

            case Ferramentas.Procurar:
            {
                if (resultado.StartsWith("Nenhum arquivo casa", StringComparison.Ordinal))
                    return "nenhum arquivo";

                var m = Regex.Match(resultado, @"^(\d+) arquivo\(s\)");
                return m.Success ? Plural(m.Groups[1].Value, "arquivo", "arquivos") : null;
            }

            case Ferramentas.Buscar:
            {
                if (resultado.StartsWith("Nada casa", StringComparison.Ordinal))
                    return "nenhum acerto";

                var m = Regex.Match(resultado, @"^(\d+) acerto\(s\) em (\d+) arquivo\(s\)");
                return m.Success
                    ? $"{Plural(m.Groups[1].Value, "acerto", "acertos")} em "
                      + Plural(m.Groups[2].Value, "arquivo", "arquivos")
                    : null;
            }

            case Ferramentas.LerEmail:
            {
                var m = Regex.Match(resultado, @"^TEXTO ORIGINAL da conversa — (\d+) mensagem");
                return m.Success ? Plural(m.Groups[1].Value, "mensagem lida", "mensagens lidas") : null;
            }

            case Ferramentas.Shell:
            {
                if (resultado.StartsWith("Comando executado com sucesso (sem saída)", StringComparison.Ordinal))
                    return "sem saída";

                int linhas = resultado.Split('\n').Count(l => l.Trim().Length > 0);
                return "saída: " + Plural(linhas.ToString(), "linha", "linhas");
            }

            default:
                return null;
        }
    }

    /// <summary>Teto de cada lado do antes e depois. Mesmo motivo do teto da saída bruta.</summary>
    public const int TetoDaTroca = 2000;

    /// <summary>
    /// O antes e depois de uma edição, para a seção ANTES E DEPOIS do tooltip.
    /// <para>
    /// Sai dos ARGUMENTOS, e não do arquivo: é exatamente o trecho que o modelo pediu para trocar,
    /// que é o que foi autorizado no card. Vale também quando a edição falhou ou foi recusada —
    /// ver o que se tentou trocar é o que explica a falha. Nulo para qualquer outra ferramenta:
    /// o <c>write</c> não guarda o conteúdo anterior, e um "antes" inventado seria pior que nenhum.
    /// </para>
    /// </summary>
    public static TrocaDeTexto? TrocaDaEdicao(string ferramenta, string argumentosJson)
    {
        if (ferramenta != Ferramentas.Editar) return null;

        string antes = StringDe(argumentosJson, "old_string");
        if (antes.Length == 0) return null;

        return new TrocaDeTexto(ComTetoDeTroca(antes), ComTetoDeTroca(StringDe(argumentosJson, "new_string")));
    }

    private static string ComTetoDeTroca(string trecho)
    {
        string t = trecho.Replace("\r", "");
        return t.Length <= TetoDaTroca ? t : t[..TetoDaTroca] + "…";
    }

    /// <summary>Teto da saída guardada para o tooltip. O registro tem 500 entradas e vive em memória.</summary>
    public const int TetoDaSaidaBruta = 4000;

    /// <summary>
    /// A saída como saiu, para a seção SAÍDA BRUTA do tooltip (§4.6 ii) — só das ferramentas
    /// cuja saída é o próprio resultado: comando, busca por nome e busca no conteúdo.
    /// <para>
    /// <c>read</c> fica de fora de propósito: a saída dele é o conteúdo do arquivo, e guardar
    /// isso em cada linha do registro seria uma segunda cópia do arquivo em memória. <c>mail</c>
    /// também: veredito de e-mail não tem por que ficar num tooltip além do que o chat já mostra.
    /// </para>
    /// </summary>
    public static string? SaidaBruta(string ferramenta, string? resultado)
    {
        if (string.IsNullOrWhiteSpace(resultado)) return null;
        if (ferramenta is not (Ferramentas.Shell or Ferramentas.Procurar or Ferramentas.Buscar)) return null;

        string t = resultado.Replace("\r", "").TrimEnd();
        return t.Length <= TetoDaSaidaBruta ? t : t[..TetoDaSaidaBruta] + "\n…";
    }

    private static Artifact? Build(string ferramenta, string argumentosJson, string resultado)
    {
        bool recusado = resultado.Contains(TextoRecusa, StringComparison.Ordinal);
        bool falhou = recusado || resultado.StartsWith("ERRO", StringComparison.OrdinalIgnoreCase);

        switch (ferramenta)
        {
            case Ferramentas.Gravar:
            {
                string caminho = CaminhoDe(argumentosJson);
                if (caminho.Length == 0) return null;
                if (recusado) return new Artifact(ArtifactKind.Denied, ferramenta, caminho, true, "gravação recusada");

                string? detalhe = TamanhoDoConteudo(argumentosJson);
                return new Artifact(ArtifactKind.FileWritten, ferramenta, caminho, falhou,
                    falhou ? PrimeiraLinha(resultado) : detalhe);
            }

            case Ferramentas.Editar:
            {
                // Editar GRAVA o arquivo. Sem este caso a edição não deixava artefato: o capítulo
                // da memória esquecia que o arquivo mudou, e a tela mostrava só "edit".
                string caminho = CaminhoDe(argumentosJson);
                if (caminho.Length == 0) return null;
                if (recusado) return new Artifact(ArtifactKind.Denied, ferramenta, caminho, true, "edição recusada");

                return new Artifact(ArtifactKind.FileWritten, ferramenta, caminho, falhou,
                    falhou ? PrimeiraLinha(resultado) : LinhasTrocadas(argumentosJson, resultado));
            }

            case Ferramentas.Ler:
            {
                string caminho = CaminhoDe(argumentosJson);
                if (caminho.Length == 0) return null;
                if (recusado) return new Artifact(ArtifactKind.Denied, ferramenta, caminho, true, "leitura recusada");

                return new Artifact(ArtifactKind.FileRead, ferramenta, caminho, falhou,
                    falhou ? PrimeiraLinha(resultado) : null);
            }

            case Ferramentas.Shell:
            {
                string comando = StringDe(argumentosJson, "command");
                if (comando.Length == 0) return null;
                if (recusado) return new Artifact(ArtifactKind.Denied, ferramenta, comando, true, "comando recusado");

                // O erro literal é o que importa quando falha: "acesso negado" e "arquivo não
                // encontrado" pedem correções opostas, e um resumo apaga a diferença.
                return new Artifact(ArtifactKind.CommandRun, ferramenta, comando, falhou,
                    falhou ? PrimeiraLinha(resultado) : null);
            }

            case Ferramentas.Habilidade:
            {
                // O literal aqui e a chamada: nome da habilidade mais os argumentos que ela
                // recebeu. So o nome da ferramenta nao serve — Ferramentas.Habilidade repetido na
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

    /// <summary>
    /// "+3 linhas, −1 linha": o quanto a edição mexeu, contado dos trechos que o modelo mandou e
    /// multiplicado pelas trocas que a ferramenta confirmou. É o formato do registro (§6.4 e).
    /// </summary>
    private static string? LinhasTrocadas(string argumentosJson, string resultado)
    {
        string de = StringDe(argumentosJson, "old_string");
        string para = StringDe(argumentosJson, "new_string");
        if (de.Length == 0) return null;

        var trocas = Regex.Match(resultado ?? "", @"SUCESSO: (\d+) troca");
        int vezes = trocas.Success ? int.Parse(trocas.Groups[1].Value) : 1;

        int saem = ContarLinhas(de) * vezes;
        int entram = ContarLinhas(para) * vezes;

        return $"+{Plural(entram.ToString(), "linha", "linhas")}, −{Plural(saem.ToString(), "linha", "linhas")}";
    }

    private static int ContarLinhas(string trecho) =>
        trecho.Length == 0 ? 0 : trecho.Replace("\r", "").TrimEnd('\n').Split('\n').Length;

    /// <summary>"*.cs em C:\projeto" — o padrão e a pasta resolvida, como a ferramenta resolve.</summary>
    private static string BuscaDe(string argumentosJson, bool comFiltro)
    {
        string padrao = StringDe(argumentosJson, "pattern");
        if (padrao.Length == 0) return "";

        string raiz = PathArgumentRepair.Normalize(StringDe(argumentosJson, "path"));
        if (string.IsNullOrWhiteSpace(raiz))
            raiz = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        string filtro = comFiltro ? StringDe(argumentosJson, "glob") : "";
        return filtro.Length == 0 ? $"{padrao} em {raiz}" : $"{padrao} em {raiz} ({filtro})";
    }

    /// <summary>"hoje • urgência maxima • de: fulano" — o período e os filtros pedidos.</summary>
    private static string ConsultaDeEmail(string argumentosJson)
    {
        string periodo = StringDe(argumentosJson, "periodo");
        var partes = new List<string> { periodo.Length == 0 ? "hoje" : periodo };

        string urgencia = StringDe(argumentosJson, "urgencia");
        if (urgencia.Length > 0 && urgencia != "todas") partes.Add("urgência " + urgencia);

        string remetente = StringDe(argumentosJson, "remetente");
        if (remetente.Length > 0) partes.Add("de: " + remetente);

        string assunto = StringDe(argumentosJson, "assunto");
        if (assunto.Length > 0) partes.Add("assunto: " + assunto);

        return string.Join(" • ", partes);
    }

    private static string Plural(string numero, string singular, string plural) =>
        numero == "1" ? $"1 {singular}" : $"{numero} {plural}";

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
