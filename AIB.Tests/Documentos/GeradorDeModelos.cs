using System;
using System.IO;
using System.Text.Json;
using AIB.Services.Documentos;
using ClosedXML.Excel;
using Xunit;

namespace AIB.Tests;

/// <summary>
/// Refaz os modelos que vêm com o AIB (<c>AIBWindows/modelos</c>) pela própria ferramenta doc —
/// prova de que ela faz o que os modelos ensinam. Não roda na suíte: só com
/// <c>$env:AIB_MODELOS="&lt;caminho de AIBWindows\modelos&gt;"</c>. Os .txt de descrição são à mão.
/// </summary>
public class GeradorDeModelos
{
    private static JsonElement J(string s)
    {
        using var d = JsonDocument.Parse(s);
        return d.RootElement.Clone();
    }

    [Fact]
    public void Gera()
    {
        string? pasta = Environment.GetEnvironmentVariable("AIB_MODELOS");
        if (pasta == null) return;
        Directory.CreateDirectory(pasta);

        // ── painel de indicadores ──
        string painel = Path.Combine(pasta, "painel-de-indicadores.xlsx");
        if (File.Exists(painel)) File.Delete(painel);
        EditorDePlanilha.PlanejarCriacao(painel, J("""
            {"sheets":[
              {"name":"Painel","rows":[[""]]},
              {"name":"Dados","rows":[
                ["ID","Abertura","Conclusão","Categoria","Responsável","Status","Horas","Prazo"],
                [1001,"01/10/2026 08:30","01/10/2026 11:00","Hardware","Felipe","Concluída",2.5,"No prazo"],
                [1002,"01/10/2026 09:10","02/10/2026 10:00","Rede","Gabriel","Concluída",6,"Atrasado"],
                [1003,"02/10/2026 14:00","02/10/2026 15:30","Acesso","Marcos","Concluída",1.5,"No prazo"],
                [1004,"03/10/2026 10:00","03/10/2026 16:00","Sistemas","Lucas","Concluída",5,"No prazo"],
                [1005,"06/10/2026 08:00","","Hardware","Felipe","Em andamento",3,""],
                [1006,"06/10/2026 11:20","07/10/2026 09:00","Rede","Gabriel","Concluída",4,"No prazo"],
                [1007,"07/10/2026 13:45","","Sistemas","Lucas","Aberta","",""],
                [1008,"08/10/2026 09:30","08/10/2026 10:15","Acesso","Marcos","Concluída",0.75,"No prazo"]]}]}
            """)).Gravar();

        EditorDePlanilha.Planejar(painel, J("""
            [{"sheet":"Painel","range":"A1:G20","style":{"font":"Segoe UI","size":10,"bold":false}},
             {"sheet":"Painel","gridlines":false},
             {"sheet":"Painel","columns":"A","width":3},{"sheet":"Painel","columns":"B","width":30},
             {"sheet":"Painel","columns":"C:D","width":14},{"sheet":"Painel","columns":"E","width":18},
             {"sheet":"Painel","columns":"F","width":38},{"sheet":"Painel","columns":"G","width":3},
             {"sheet":"Painel","rows":"1","height":44},{"sheet":"Painel","rows":"2","height":20},
             {"sheet":"Painel","rows":"3","height":10},{"sheet":"Painel","rows":"4:9","height":24},
             {"sheet":"Painel","rows":"10","height":10},{"sheet":"Painel","rows":"11:17","height":24},
             {"sheet":"Painel","rows":"18","height":10},{"sheet":"Painel","rows":"19","height":34},

             {"sheet":"Painel","cell":"B1","value":"PAINEL DE INDICADORES","style":{"bold":true,"size":20,"color":"#1F3864","valign":"middle"}},
             {"sheet":"Painel","merge":"B1:F1"},
             {"sheet":"Painel","cell":"B2","value":"Período: 01/10/2026 a 31/10/2026 · fonte: aba Dados","style":{"italic":true,"color":"#7F7F7F","valign":"top"}},
             {"sheet":"Painel","merge":"B2:F2"},

             {"sheet":"Painel","cell":"B4","value":"INDICADORES GERAIS","style":{"bold":true,"color":"branco","fill":"#1F3864","valign":"middle","indent":1}},
             {"sheet":"Painel","merge":"B4:F4"},
             {"sheet":"Painel","cell":"B5","value":"Indicador"},{"sheet":"Painel","cell":"C5","value":"Valor"},
             {"sheet":"Painel","cell":"D5","value":"Meta"},{"sheet":"Painel","cell":"E5","value":"Situação"},
             {"sheet":"Painel","cell":"F5","value":"Como se calcula"},
             {"sheet":"Painel","range":"B5:F5","style":{"bold":true,"color":"#1F3864","fill":"#D9E1F2","valign":"middle","indent":1,"border":"bottom"}},

             {"sheet":"Painel","cell":"B6","value":"Demandas registradas"},
             {"sheet":"Painel","cell":"C6","value":"=COUNTA(Dados!A2:A200)"},
             {"sheet":"Painel","cell":"D6","value":"—"},{"sheet":"Painel","cell":"E6","value":"—"},
             {"sheet":"Painel","cell":"F6","value":"Linhas preenchidas na aba Dados"},
             {"sheet":"Painel","cell":"B7","value":"Taxa de conclusão"},
             {"sheet":"Painel","cell":"C7","value":"=IFERROR(COUNTIF(Dados!F2:F200,\"Concluída\")/C6,0)"},
             {"sheet":"Painel","cell":"D7","value":0.9},
             {"sheet":"Painel","cell":"E7","value":"=IF(C7>=D7,\"Na meta\",\"Abaixo da meta\")"},
             {"sheet":"Painel","cell":"F7","value":"Concluídas ÷ registradas"},
             {"sheet":"Painel","cell":"B8","value":"Entregas no prazo"},
             {"sheet":"Painel","cell":"C8","value":"=IFERROR(COUNTIF(Dados!H2:H200,\"No prazo\")/COUNTIF(Dados!F2:F200,\"Concluída\"),0)"},
             {"sheet":"Painel","cell":"D8","value":0.95},
             {"sheet":"Painel","cell":"E8","value":"=IF(C8>=D8,\"Na meta\",\"Abaixo da meta\")"},
             {"sheet":"Painel","cell":"F8","value":"No prazo ÷ concluídas"},
             {"sheet":"Painel","cell":"B9","value":"Tempo médio de solução (h)"},
             {"sheet":"Painel","cell":"C9","value":"=IFERROR(AVERAGEIF(Dados!F2:F200,\"Concluída\",Dados!G2:G200),0)"},
             {"sheet":"Painel","cell":"D9","value":4},
             {"sheet":"Painel","cell":"E9","value":"=IF(C9<=D9,\"Na meta\",\"Abaixo da meta\")"},
             {"sheet":"Painel","cell":"F9","value":"Média de horas das concluídas (meta: até)"},
             {"sheet":"Painel","range":"B6:F9","style":{"valign":"middle","indent":1,"border":"bottom"}},
             {"sheet":"Painel","range":"C6:E9","style":{"align":"center","indent":0}},
             {"sheet":"Painel","range":"C7:D8","style":{"number_format":"0%"}},
             {"sheet":"Painel","range":"C9:D9","style":{"number_format":"0.0"}},
             {"sheet":"Painel","range":"C6:C9","style":{"bold":true}},
             {"sheet":"Painel","range":"F6:F9","style":{"italic":true,"color":"#7F7F7F"}},
             {"sheet":"Painel","range":"E6:E9","conditional":{"when":"contains","value":"Na meta","style":{"fill":"#E2EFDA","color":"#375623","bold":true}}},
             {"sheet":"Painel","range":"E6:E9","conditional":{"when":"contains","value":"Abaixo","style":{"fill":"#FCE4D6","color":"#C00000","bold":true}}},

             {"sheet":"Painel","cell":"B11","value":"POR CATEGORIA","style":{"bold":true,"color":"branco","fill":"#1F3864","valign":"middle","indent":1}},
             {"sheet":"Painel","merge":"B11:F11"},
             {"sheet":"Painel","cell":"B12","value":"Categoria"},{"sheet":"Painel","cell":"C12","value":"Demandas"},
             {"sheet":"Painel","cell":"D12","value":"% do total"},{"sheet":"Painel","cell":"E12","value":"Horas"},
             {"sheet":"Painel","cell":"F12","value":"Volume"},
             {"sheet":"Painel","range":"B12:F12","style":{"bold":true,"color":"#1F3864","fill":"#D9E1F2","valign":"middle","indent":1,"border":"bottom"}},
             {"sheet":"Painel","cell":"B13","value":"Hardware"},{"sheet":"Painel","cell":"B14","value":"Rede"},
             {"sheet":"Painel","cell":"B15","value":"Sistemas"},{"sheet":"Painel","cell":"B16","value":"Acesso"},
             {"sheet":"Painel","range":"C13:C16","value":"=COUNTIF(Dados!$D$2:$D$200,B{row})"},
             {"sheet":"Painel","range":"D13:D16","value":"=IFERROR(C{row}/$C$6,0)"},
             {"sheet":"Painel","range":"E13:E16","value":"=SUMIF(Dados!$D$2:$D$200,B{row},Dados!$G$2:$G$200)"},
             {"sheet":"Painel","range":"F13:F16","value":"=C{row}"},
             {"sheet":"Painel","range":"B13:F16","style":{"valign":"middle","indent":1,"border":"bottom"}},
             {"sheet":"Painel","range":"C13:E16","style":{"align":"center","indent":0}},
             {"sheet":"Painel","range":"D13:D17","style":{"number_format":"0%"}},
             {"sheet":"Painel","range":"E13:E17","style":{"number_format":"0.0"}},
             {"sheet":"Painel","range":"F13:F16","conditional":{"bar":"#8EA9DB"}},
             {"sheet":"Painel","cell":"B17","value":"TOTAL"},
             {"sheet":"Painel","cell":"C17","value":"=SUM(C13:C16)"},
             {"sheet":"Painel","cell":"D17","value":"=SUM(D13:D16)"},
             {"sheet":"Painel","cell":"E17","value":"=SUM(E13:E16)"},
             {"sheet":"Painel","range":"B17:F17","style":{"bold":true,"valign":"middle","indent":1,"border":["top","double"]}},
             {"sheet":"Painel","range":"C17:E17","style":{"align":"center","indent":0}},

             {"sheet":"Painel","cell":"B19","value":"Como usar: preencha a aba Dados (uma linha por demanda); o painel se atualiza sozinho. A Situação compara o Valor com a Meta.","style":{"italic":true,"color":"#7F7F7F","wrap":true,"valign":"top"}},
             {"sheet":"Painel","merge":"B19:F19"},
             {"sheet":"Painel","print":{"orientation":"portrait","fit":"width","area":"B1:F19","margins":"estreita","center":true}},

             {"sheet":"Dados","range":"A1:H200","style":{"font":"Segoe UI","size":10,"bold":false,"valign":"middle"}},
             {"sheet":"Dados","columns":"A","width":8},{"sheet":"Dados","columns":"B:C","width":18},
             {"sheet":"Dados","columns":"D","width":14},{"sheet":"Dados","columns":"E","width":16},
             {"sheet":"Dados","columns":"F","width":16},{"sheet":"Dados","columns":"G","width":9},
             {"sheet":"Dados","columns":"H","width":12},
             {"sheet":"Dados","rows":"1","height":26},{"sheet":"Dados","rows":"2:9","height":20},
             {"sheet":"Dados","range":"A1:H1","style":{"bold":true,"color":"branco","fill":"#1F3864","indent":1}},
             {"sheet":"Dados","range":"A2:H9","style":{"border":"bottom"}},
             {"sheet":"Dados","range":"A2:A200","style":{"align":"center"}},
             {"sheet":"Dados","range":"B2:C200","style":{"number_format":"dd/mm/yyyy hh:mm","align":"center"}},
             {"sheet":"Dados","range":"D2:F200","style":{"indent":1}},
             {"sheet":"Dados","range":"G2:G200","style":{"number_format":"0.0","align":"center"}},
             {"sheet":"Dados","range":"H2:H200","style":{"align":"center"}},
             {"sheet":"Dados","range":"D2:D200","validation":{"list":"=Painel!$B$13:$B$16"}},
             {"sheet":"Dados","range":"F2:F200","validation":{"list":["Aberta","Em andamento","Concluída"]}},
             {"sheet":"Dados","range":"H2:H200","validation":{"list":["No prazo","Atrasado"]}},
             {"sheet":"Dados","range":"F2:F200","conditional":{"when":"=","value":"Concluída","style":{"color":"#375623","bold":true}}},
             {"sheet":"Dados","range":"H2:H200","conditional":{"when":"=","value":"Atrasado","style":{"fill":"#FCE4D6","color":"#C00000","bold":true}}},
             {"sheet":"Dados","freeze":"A2"},
             {"sheet":"Dados","filter":"A1:H200"},
             {"sheet":"Dados","print":{"orientation":"landscape","fit":"width","repeat_rows":"1","margins":"estreita"}}]
            """), null).Gravar();

        // ── rubrica de avaliação ──
        string rubrica = Path.Combine(pasta, "rubrica-de-avaliacao.xlsx");
        if (File.Exists(rubrica)) File.Delete(rubrica);
        EditorDePlanilha.PlanejarCriacao(rubrica, J("""{"sheets":[{"name":"Avaliação","rows":[[""]]}]}""")).Gravar();
        EditorDePlanilha.Planejar(rubrica, J("""
            [{"range":"A1:H26","style":{"font":"Segoe UI","size":10,"bold":false}},
             {"gridlines":false},
             {"columns":"A","width":3},{"columns":"B","width":26},{"columns":"C","width":52},
             {"columns":"D:F","width":11},{"columns":"G","width":3},{"columns":"H","width":3},
             {"rows":"1","height":44},{"rows":"2:5","height":22},{"rows":"6","height":10},
             {"rows":"7","height":24},{"rows":"8:12","height":46},{"rows":"13:14","height":24},
             {"rows":"15","height":10},{"rows":"16","height":24},{"rows":"17:21","height":22},
             {"rows":"22","height":10},{"rows":"23","height":34},

             {"cell":"B1","value":"AVALIAÇÃO DE FORNECEDOR","style":{"bold":true,"size":20,"color":"#44546A","valign":"middle"}},
             {"merge":"B1:F1"},
             {"cell":"B2","value":"FORNECEDOR"},{"cell":"B3","value":"AVALIADOR"},
             {"cell":"B4","value":"DATA"},{"cell":"B5","value":"CONTRATO / PEDIDO"},
             {"range":"B2:B5","style":{"bold":true,"color":"branco","fill":"#222A35","valign":"middle","indent":1,"border":"all"}},
             {"range":"C2:F2","value":null},{"merge":"C2:F2"},{"merge":"C3:F3"},{"merge":"C4:F4"},{"merge":"C5:F5"},
             {"range":"C2:F5","style":{"valign":"middle","indent":1,"border":"all"}},
             {"cell":"C4","style":{"number_format":"data","align":"left"}},

             {"cell":"B7","value":"CRITÉRIO"},{"cell":"C7","value":"O QUE SE OBSERVA"},
             {"cell":"D7","value":"NOTA (0–4)"},{"cell":"E7","value":"PESO"},{"cell":"F7","value":"PONTOS"},
             {"range":"B7:F7","style":{"bold":true,"color":"branco","fill":"#44546A","valign":"middle","border":"all"}},
             {"range":"B7:C7","style":{"indent":1}},{"range":"D7:F7","style":{"align":"center"}},

             {"cell":"B8","value":"Prazo de entrega"},{"cell":"C8","value":"Entrega na data combinada; avisa com antecedência quando há atraso."},{"cell":"E8","value":3},
             {"cell":"B9","value":"Qualidade"},{"cell":"C9","value":"O produto ou serviço atende à especificação sem retrabalho."},{"cell":"E9","value":3},
             {"cell":"B10","value":"Preço"},{"cell":"C10","value":"Preço compatível com o mercado e sem custos fora do combinado."},{"cell":"E10","value":2},
             {"cell":"B11","value":"Atendimento"},{"cell":"C11","value":"Responde rápido, resolve problemas e mantém um contato definido."},{"cell":"E11","value":1},
             {"cell":"B12","value":"Documentação"},{"cell":"C12","value":"Notas, certidões e laudos completos e no prazo."},{"cell":"E12","value":1},
             {"range":"F8:F12","value":"=IF(D{row}=\"\",0,D{row}*E{row})"},
             {"range":"B8:F12","style":{"valign":"middle","border":"all"}},
             {"range":"B8:B12","style":{"bold":true,"indent":1}},
             {"range":"C8:C12","style":{"wrap":true,"indent":1,"color":"#404040"}},
             {"range":"D8:D12","style":{"align":"center","bold":true,"fill":"#F2F2F2"}},
             {"range":"E8:F12","style":{"align":"center"}},
             {"range":"D8:D12","validation":{"list":["0","1","2","3","4"]}},

             {"cell":"B13","value":"TOTAL PONDERADO"},
             {"cell":"F13","value":"=SUM(F8:F12)"},
             {"cell":"B14","value":"NOTA FINAL (0–4)"},
             {"cell":"F14","value":"=IFERROR(F13/SUM(E8:E12),0)"},
             {"merge":"B13:E13"},{"merge":"B14:E14"},
             {"range":"B13:F14","style":{"bold":true,"valign":"middle","border":"all"}},
             {"range":"B13:B14","style":{"align":"right","indent":1,"color":"branco","fill":"#44546A"}},
             {"range":"F13:F14","style":{"align":"center","number_format":"0.0"}},

             {"cell":"B16","value":"ESCALA"},{"cell":"C16","value":"SIGNIFICADO"},{"cell":"D16","value":"RESULTADO"},
             {"range":"D16:F16","value":null},{"cell":"D16","value":"RESULTADO"},{"merge":"D16:F16"},
             {"range":"B16:F16","style":{"bold":true,"color":"branco","fill":"#222A35","valign":"middle","indent":1,"border":"all"}},
             {"cell":"B17","value":"4 — Excelente"},{"cell":"C17","value":"Superou o combinado"},
             {"cell":"B18","value":"3 — Bom"},{"cell":"C18","value":"Cumpriu o combinado"},
             {"cell":"B19","value":"2 — Regular"},{"cell":"C19","value":"Cumpriu com falhas pequenas"},
             {"cell":"B20","value":"1 — Fraco"},{"cell":"C20","value":"Falhas que exigiram correção"},
             {"cell":"B21","value":"0 — Inaceitável"},{"cell":"C21","value":"Não cumpriu"},
             {"cell":"D17","value":"=IF(COUNT(D8:D12)=0,\"\",IF(F14>=3.5,\"APROVADO\",IF(F14>=2.5,\"APROVADO COM RESSALVAS\",\"REPROVADO\")))"},
             {"range":"E17:F21","value":null},{"merge":"D17:F21"},
             {"range":"B17:F21","style":{"valign":"middle","indent":1,"border":"all"}},
             {"range":"B17:C21","style":{"fill":"#F2F2F2"}},
             {"cell":"D17","style":{"bold":true,"size":14,"align":"center","indent":0,"wrap":true}},
             {"range":"D17:F21","conditional":{"when":"=","value":"APROVADO","style":{"fill":"#E2EFDA","color":"#375623"}}},
             {"range":"D17:F21","conditional":{"when":"contains","value":"RESSALVAS","style":{"fill":"#FFF2CC","color":"#7F6000"}}},
             {"range":"D17:F21","conditional":{"when":"=","value":"REPROVADO","style":{"fill":"#FCE4D6","color":"#C00000"}}},

             {"cell":"B23","value":"Como usar: escolha a nota de cada critério na lista; o peso diz quanto ele vale. A nota final é a média ponderada, e o resultado sai dela.","style":{"italic":true,"color":"#7F7F7F","wrap":true,"valign":"top"}},
             {"merge":"B23:F23"},
             {"freeze":"A7"},
             {"print":{"orientation":"portrait","fit":"page","area":"B1:F23","margins":"estreita","center":true}}]
            """), null).Gravar();

        // ── ata de reunião ──
        string ata = Path.Combine(pasta, "ata-de-reuniao.docx");
        if (File.Exists(ata)) File.Delete(ata);
        EditorDeDocx.PlanejarCriacao(ata, J("""
            {"blocks":[
              {"style":"Heading1","text":"Ata de reunião"},
              {"text":"Data: __/__/____     Horário: __:__ às __:__     Local: ____________"},
              {"text":"Participantes: ____________________________________________"},
              {"style":"Heading2","text":"Pauta"},
              {"list":["Assunto 1","Assunto 2","Assunto 3"]},
              {"style":"Heading2","text":"Discussão"},
              {"text":"Resumo do que foi discutido em cada assunto da pauta."},
              {"style":"Heading2","text":"Decisões e encaminhamentos"},
              {"table":[["O quê","Responsável","Prazo"],["","",""],["","",""],["","",""]]},
              {"style":"Heading2","text":"Próxima reunião"},
              {"text":"Data, horário e assuntos previstos."}]}
            """)).Gravar();
    }
}
