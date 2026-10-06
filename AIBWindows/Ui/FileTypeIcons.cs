using System;
using System.Collections.Generic;
using System.Windows.Media;

namespace AIB.Ui;

/// <summary>
/// Desenhos por TIPO de arquivo, para a aba de arquivos do painel — §6.2(a).
/// <para>
/// Deliberadamente diferente do <see cref="ToolIcons"/>. Aqui o ícone diz o que o arquivo É; lá
/// ele diz o que foi FEITO. Numa lista de arquivos a pergunta é "que arquivo é este"; no meio de
/// uma cadeia de ações é "o que ele fez". Usar o mesmo ícone nos dois lugares responderia a
/// pergunta errada em um deles.
/// </para>
/// </summary>
public static class FileTypeIcons
{
    private const string FolhaBase = "M4,2 H12 L16,6 V20 H4 Z M12,2 V6 H16";

    // Texto: linhas dentro da folha.
    private const string Texto = FolhaBase + " M6.6,10.4 H13.4 M6.6,13 H13.4 M6.6,15.6 H11";

    // Markdown: a seta do "M↓".
    private const string Markdown = FolhaBase + " M6.4,15.6 V10.4 L8.6,13 L10.8,10.4 V15.6 M13,10.8 V15 M11.6,13.6 L13,15.2 L14.4,13.6";

    // Imagem: moldura com montanha e sol.
    private const string Imagem = "M3,4.5 H17 V17.5 H3 Z M3,14 L7.4,9.6 L11,13.2 L13.2,11 L17,14.8 M12.6,7.8 A1.1,1.1 0 1 1 12.6,8.0 Z";

    // Documento com sigla: folha e uma faixa embaixo, onde a sigla apareceria.
    private const string Documento = FolhaBase + " M6.4,14 H13.6 V18 H6.4 Z";

    // Código: os sinais de maior e menor.
    private const string Codigo = FolhaBase + " M8.4,11.4 L6.4,13.4 L8.4,15.4 M11.6,11.4 L13.6,13.4 L11.6,15.4";

    // Planilha: grade.
    private const string Planilha = FolhaBase + " M6,11 H14 M6,14 H14 M6,17 H14 M9.4,11 V17.6 M12,11 V17.6";

    // Sem extensão: folha lisa.
    private const string Lisa = FolhaBase;

    private static readonly Dictionary<string, Geometry> Cache = new(StringComparer.Ordinal);

    /// <summary>Desenho pela extensão, sem o ponto e em minúsculas.</summary>
    public static Geometry De(string? extensao) => Obter((extensao ?? "").ToLowerInvariant() switch
    {
        "txt" or "log" or "ini" or "cfg" or "env" => Texto,
        "md" or "markdown" => Markdown,
        "png" or "jpg" or "jpeg" or "webp" or "gif" or "bmp" or "svg" or "ico" => Imagem,
        "pdf" or "docx" or "doc" or "odt" or "rtf" => Documento,
        "xlsx" or "xls" or "csv" or "ods" => Planilha,
        "cs" or "js" or "ts" or "py" or "json" or "xml" or "xaml" or "html" or "css"
            or "ps1" or "sh" or "yml" or "yaml" or "sql" or "rs" or "go" or "java" => Codigo,
        "" => Lisa,
        _ => Texto
    });

    private static Geometry Obter(string dados)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue(dados, out var pronto)) return pronto;

            var geo = Geometry.Parse(dados);
            geo.Freeze();
            Cache[dados] = geo;
            return geo;
        }
    }
}
