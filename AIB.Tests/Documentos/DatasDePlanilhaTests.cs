using System;
using System.IO;
using System.Text.Json;
using AIB.Services.Documentos;
using ClosedXML.Excel;
using FluentAssertions;
using Xunit;

namespace AIB.Tests;

/// <summary>
/// Data escrita como o brasileiro escreve vira data do Excel. Visto em 26/10/2026: no painel da
/// Ellen (dashboard-tarefas-ti-mock.xlsx), as colunas de abertura e conclusão saíram como TEXTO
/// ('05/10/2026 08:30'), porque só 'aaaa-mm-dd' virava data; nenhuma conta, filtro de data ou
/// ordenação funcionava nelas.
/// </summary>
public class DatasDePlanilhaTests : IDisposable
{
    private readonly string _pasta = Path.Combine(Path.GetTempPath(), "aib-datas-" + Guid.NewGuid().ToString("N"));

    public DatasDePlanilhaTests() => Directory.CreateDirectory(_pasta);

    public void Dispose()
    {
        try { Directory.Delete(_pasta, true); } catch { }
    }

    private static JsonElement Json(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private IXLCell Escrito(string valor, out XLWorkbook wb)
    {
        string c = Path.Combine(_pasta, Guid.NewGuid().ToString("N") + ".xlsx");
        string conteudo = JsonSerializer.Serialize(new { sheets = new[] { new { name = "D", rows = new[] { new object[] { "Quando" }, new object[] { valor } } } } });
        EditorDePlanilha.PlanejarCriacao(c, Json(conteudo)).Gravar();
        wb = new XLWorkbook(c);
        return wb.Worksheet(1).Cell("A2");
    }

    [Theory]
    [InlineData("2026-10-05", 2026, 10, 5, 0, 0)]
    [InlineData("05/10/2026", 2026, 10, 5, 0, 0)]
    [InlineData("5/10/2026", 2026, 10, 5, 0, 0)]
    [InlineData("05/10/2026 08:30", 2026, 10, 5, 8, 30)]
    [InlineData("2026-10-05T08:30", 2026, 10, 5, 8, 30)]
    public void DataComoOBrasileiroEscreve_ViraData(string valor, int a, int m, int d, int h, int min)
    {
        var cel = Escrito(valor, out var wb);
        using (wb)
        {
            cel.Value.IsDateTime.Should().BeTrue(valor);
            cel.GetDateTime().Should().Be(new DateTime(a, m, d, h, min, 0));
        }
    }

    [Fact]
    public void DataComHora_ApareceComAHora()
    {
        var cel = Escrito("05/10/2026 08:30", out var wb);
        using (wb) cel.Style.NumberFormat.NumberFormatId.Should().Be(22, "o formato embutido de data e hora, que o Excel mostra na região do usuário");
    }

    [Fact]
    public void DataSemHora_ApareceSoADia()
    {
        var cel = Escrito("05/10/2026", out var wb);
        using (wb) cel.Style.NumberFormat.NumberFormatId.Should().Be(14, "o formato embutido de data curta");
    }

    [Theory]
    [InlineData("31/02/2026")]
    [InlineData("05/10")]
    [InlineData("10/2026")]
    [InlineData("Chamado 05/10/2026")]
    public void OQueNaoEDataInteira_FicaTexto(string valor)
    {
        var cel = Escrito(valor, out var wb);
        using (wb) cel.GetString().Should().Be(valor);
    }
}
