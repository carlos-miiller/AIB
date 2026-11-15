using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using Xdr = DocumentFormat.OpenXml.Drawing.Spreadsheet;

namespace AIB.Services.Documentos;

/// <summary>
/// O que o ClosedXML descarta ao regravar uma planilha e o AIB devolve depois: os botões e as
/// formas desenhadas (o "SALVAR" que roda a macro) e o nome interno de cada aba (o codeName
/// "Planilha1" que o VBA usa). As macros em si o ClosedXML já mantém, byte a byte.
/// <para>
/// Pedido do usuário (15/11/2026): editar .xlsm. Medido numa cópia do inventário de TI dele: o
/// ClosedXML 0.105 manteve o vbaProject.bin idêntico, mas apagou o botão "SALVAR" (ligado a
/// Módulo1.CadastrarItem) e o codeName das seis abas.
/// </para>
/// <para>
/// O original é guardado em memória no plano: a gravação devolve as partes dele, e não as do
/// disco naquela hora.
/// </para>
/// </summary>
internal sealed class RestauroDePlanilha
{
    private readonly byte[] _original;

    /// <summary>Nome interno (codeName) de cada aba, pelo nome da aba no original.</summary>
    public IReadOnlyDictionary<string, string> CodeNames { get; }

    /// <summary>Abas com botão ou forma desenhada, e quantas formas cada uma tem.</summary>
    public IReadOnlyDictionary<string, int> Formas { get; }

    public bool TemMacros { get; }

    public int TotalDeFormas => Formas.Values.Sum();

    private RestauroDePlanilha(byte[] original, Dictionary<string, string> codeNames, Dictionary<string, int> formas, bool macros)
    {
        _original = original;
        CodeNames = codeNames;
        Formas = formas;
        TemMacros = macros;
    }

    /// <summary>O que restaurar do original. Null quando não há nada (o caso comum).</summary>
    public static RestauroDePlanilha? Ler(byte[] original)
    {
        var codeNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var formas = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        bool macros;

        try
        {
            using var doc = SpreadsheetDocument.Open(new MemoryStream(original, false), false);
            var wbPart = doc.WorkbookPart;
            if (wbPart?.Workbook.Sheets == null) return null;
            macros = wbPart.VbaProjectPart != null;

            foreach (var sheet in wbPart.Workbook.Sheets.Elements<Sheet>())
            {
                if (sheet.Name?.Value is not { } nome || sheet.Id?.Value is not { } id) continue;
                if (wbPart.GetPartById(id) is not WorksheetPart ws) continue;

                if (ws.Worksheet.SheetProperties?.CodeName?.Value is { Length: > 0 } codeName)
                    codeNames[nome] = codeName;

                int n = ContarFormas(ws.DrawingsPart);
                if (n > 0) formas[nome] = n;
            }
        }
        catch (Exception ex) when (ex is OpenXmlPackageException or InvalidDataException or IOException)
        {
            return null;
        }

        return codeNames.Count == 0 && formas.Count == 0 && !macros
            ? null
            : new RestauroDePlanilha(original, codeNames, formas, macros);
    }

    /// <summary>
    /// Formas que o ClosedXML não regrava: retângulo, grupo, conector, caixa de texto. Imagem
    /// ele mantém; gráfico a ferramenta já recusa antes.
    /// </summary>
    private static int ContarFormas(DrawingsPart? desenho)
    {
        if (desenho?.WorksheetDrawing == null) return 0;
        return desenho.WorksheetDrawing.ChildElements.Count(ancora =>
            ancora.Elements<Xdr.Shape>().Any() || ancora.Elements<Xdr.GroupShape>().Any()
            || ancora.Elements<Xdr.ConnectionShape>().Any());
    }

    /// <summary>
    /// Devolve ao arquivo <paramref name="gravado"/> os codeNames e os desenhos com formas do
    /// original. <paramref name="nomes"/> leva o nome de cada aba no original ao nome final (a
    /// edição pode ter renomeado).
    /// </summary>
    public void Aplicar(string gravado, IReadOnlyDictionary<string, string> nomes)
    {
        if (CodeNames.Count == 0 && Formas.Count == 0) return;

        using var original = SpreadsheetDocument.Open(new MemoryStream(_original, false), false);
        using var novo = SpreadsheetDocument.Open(gravado, true);
        var wbOriginal = original.WorkbookPart!;
        var wbNovo = novo.WorkbookPart!;

        foreach (var sheet in wbOriginal.Workbook.Sheets!.Elements<Sheet>())
        {
            string nome = sheet.Name!.Value!;
            if (!nomes.TryGetValue(nome, out var final)) continue;
            var destino = wbNovo.Workbook.Sheets!.Elements<Sheet>()
                .FirstOrDefault(s => string.Equals(s.Name?.Value, final, StringComparison.OrdinalIgnoreCase));
            if (destino?.Id?.Value is not { } idDestino || wbNovo.GetPartById(idDestino) is not WorksheetPart wsNovo) continue;

            if (CodeNames.TryGetValue(nome, out var codeName))
            {
                var props = wsNovo.Worksheet.SheetProperties;
                if (props == null)
                {
                    props = new SheetProperties();
                    wsNovo.Worksheet.InsertAt(props, 0);
                }
                props.CodeName = codeName;
            }

            if (Formas.ContainsKey(nome) && ((WorksheetPart)wbOriginal.GetPartById(sheet.Id!.Value!)).DrawingsPart is { } desenho)
                TrocarDesenho(wsNovo, desenho);

            wsNovo.Worksheet.Save();
        }
    }

    /// <summary>
    /// Põe na aba o desenho do original inteiro (formas e imagens que ele tinha). O desenho que o
    /// ClosedXML escreveu sai: só tinha as imagens do original, que voltam junto.
    /// </summary>
    private static void TrocarDesenho(WorksheetPart ws, DrawingsPart original)
    {
        foreach (var velho in ws.Worksheet.Elements<Drawing>().ToList()) velho.Remove();
        if (ws.DrawingsPart is { } escrito) ws.DeletePart(escrito);

        var copia = ws.AddPart(original);
        var elemento = new Drawing { Id = ws.GetIdOfPart(copia) };

        // A ordem dos filhos de <worksheet> é fixa no esquema: <drawing> vem antes destes.
        var depois = ws.Worksheet.ChildElements.FirstOrDefault(e => e is LegacyDrawing or LegacyDrawingHeaderFooter
            or DrawingHeaderFooter or Picture or OleObjects or Controls or WebPublishItems or TableParts
            or WorksheetExtensionList);
        if (depois != null) ws.Worksheet.InsertBefore(elemento, depois);
        else ws.Worksheet.Append(elemento);
    }
}
