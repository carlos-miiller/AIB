using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace AIB.Services.Documentos;

/// <summary>
/// O manual da ferramenta <c>doc</c>, devolvido por <c>action='help'</c>.
/// <para>
/// A descrição e o esquema vão ao modelo em toda requisição e por isso são curtos: dizem QUAIS
/// chaves existem, não O QUE cabe nelas. Visto em 26/10/2026: para montar um painel numa
/// planilha, a Ellen abriu o código-fonte umas 15 vezes (EstiloDePlanilha, CondicionalDePlanilha,
/// EditorDePlanilha) atrás dos nomes de cor, dos atalhos de number_format e da forma do
/// 'conditional', quase sempre antes de tentar e sem erro nenhum. O manual responde isso sob
/// pedido, sem custo nas outras requisições.
/// </para>
/// <para>
/// As listas (cores, atalhos, chaves, escalas, ícones, funções) vêm das mesmas tabelas que o
/// código usa, e os exemplos são dados: <c>ManualDoDocTests</c> roda cada um contra uma planilha
/// e um Word de verdade. Manual que mente ensina errado com autoridade.
/// </para>
/// </summary>
public static class ManualDoDoc
{
    /// <summary>Onde o exemplo se aplica, para o teste saber como rodá-lo.</summary>
    public enum Uso { Ops, OpsDoWord, Consulta, CriarPlanilha, CriarWord }

    public sealed record Exemplo(string Legenda, string Json, Uso Uso);

    private sealed record Assunto(string Nome, string[] Apelidos, string Resumo, Func<string> Texto, Exemplo[] Exemplos);

    // ─────────────────────────────────────────────────────────────────────────
    // Os exemplos (rodam nos testes: a planilha dos exemplos de edição é a de CriarPlanilha)
    // ─────────────────────────────────────────────────────────────────────────

    private const string Vendas = """
        {"sheets":[
          {"name":"Vendas","rows":[
            ["Produto","Setor","Valor","Data"],
            ["Notebook","TI",4500,"2026-09-03"],
            ["Cadeira","Adm",890.5,"2026-09-10"],
            ["Monitor","TI",1200,"2026-10-01"],
            ["Mesa","Adm",1500,"2026-10-05"],
            ["Notebook","Vendas",4500,"2026-10-20"]]},
          {"name":"Resumo","rows":[
            ["Indicador","Valor"],
            ["Total vendido","=SUM(Vendas!C2:C6)"]]}]}
        """;

    private const string Proposta = """
        {"blocks":[
          {"style":"Heading1","text":"Proposta comercial"},
          {"text":"O prazo de entrega é de 30 dias."},
          {"list":["Instalação","Treinamento"]},
          {"table":[["Item","Valor"],["Licença","R$ 100"]]}]}
        """;

    private static readonly Exemplo[] DeCelulas =
    {
        new("Um valor numa célula (texto, número, true/false):", """[{"cell":"E1","value":"Comissão"}]""", Uso.Ops),
        new("Número é número JSON, sem aspas e com ponto decimal:", """[{"cell":"C7","value":1234.56}]""", Uso.Ops),
        new("Data: texto 'aaaa-mm-dd' ou 'dd/mm/aaaa' vira data de verdade no Excel:", """[{"cell":"D7","value":"2026-11-15"}]""", Uso.Ops),
        new("Data com hora, do jeito brasileiro:", """[{"cell":"D8","value":"15/11/2026 08:30"}]""", Uso.Ops),
        new("Fórmula (começa com '='; nomes de função em INGLÊS, separador vírgula):", """[{"cell":"C8","value":"=SUM(C2:C6)"}]""", Uso.Ops),
        new("Fórmula em INGLÊS também para SE, PROCV etc.:", """[{"cell":"E2","value":"=IF(C2>1000,\"alto\",\"baixo\")"}]""", Uso.Ops),
        new("A mesma fórmula numa faixa; {row} vira o número de cada linha:", """[{"range":"E2:E6","value":"=C{row}*0.05"}]""", Uso.Ops),
        new("Um valor igual em todas as células da faixa:", """[{"range":"F2:F6","value":"pendente"}]""", Uso.Ops),
        new("Apagar o conteúdo (value null):", """[{"cell":"A6","value":null}]""", Uso.Ops),
        new("Inserir duas linhas ANTES da linha 4 (cada linha é uma lista de valores):",
            """[{"insert_rows":4,"rows":[["Mouse","TI",80,"2026-10-02"],["Teclado","TI",120,"2026-10-02"]]}]""", Uso.Ops),
        new("Inserir UMA linha (lista solta de valores também vale):", """[{"insert_rows":7,"rows":["Total","",0,""]}]""", Uso.Ops),
        new("Apagar as linhas 5 e 6 ('5:6', '5-6' ou só 5):", """[{"delete_rows":"5:6"}]""", Uso.Ops),
        new("Em outra aba ('sheet' em cada operação):", """[{"sheet":"Resumo","cell":"A3","value":"Itens"},{"sheet":"Resumo","cell":"B3","value":"=COUNTA(Vendas!A2:A6)"}]""", Uso.Ops),
    };

    private static readonly Exemplo[] DeEstilo =
    {
        new("Cabeçalho: negrito, fundo azul escuro, letra branca, centralizado, com grade:",
            """[{"range":"A1:D1","style":{"bold":true,"fill":"azul escuro","color":"branco","align":"center","border":"all"}}]""", Uso.Ops),
        new("Moeda em reais:", """[{"range":"C2:C6","style":{"number_format":"moeda"}}]""", Uso.Ops),
        new("Formato próprio do Excel (código em inglês: vírgula = milhar, ponto = decimal):",
            """[{"range":"C2:C6","style":{"number_format":"#,##0.0"}},{"range":"D2:D6","style":{"number_format":"dd/mm/yyyy"}}]""", Uso.Ops),
        new("Valor e estilo na mesma operação:",
            """[{"cell":"A8","value":"TOTAL","style":{"bold":true,"border":["top","double"]}}]""", Uso.Ops),
        new("Cor em hex, tamanho e fonte:", """[{"cell":"A1","style":{"color":"#1E293B","size":14,"font":"Arial"}}]""", Uso.Ops),
        new("Texto longo: quebrar linha e alinhar no topo:", """[{"range":"A2:A6","style":{"wrap":true,"valign":"top"}}]""", Uso.Ops),
        new("Tabela com respiro: meio da altura e recuo 1 no texto:", """[{"range":"A1:D6","style":{"valign":"middle","indent":1}}]""", Uso.Ops),
        new("Tirar negrito e bordas:", """[{"range":"A1:D1","style":{"bold":false,"border":"none"}}]""", Uso.Ops),
    };

    private static readonly Exemplo[] DeCondicional =
    {
        new("Valor maior que 1000: fundo verde claro, letra verde escura, negrito:",
            """[{"range":"C2:C6","conditional":{"when":">","value":1000,"style":{"fill":"verde claro","color":"verde escuro","bold":true}}}]""", Uso.Ops),
        new("Negativo em vermelho:", """[{"range":"C2:C6","conditional":{"when":"<","value":0,"style":{"color":"vermelho"}}}]""", Uso.Ops),
        new("Duas regras na mesma faixa, no mesmo pedido (as duas ficam):",
            """[{"range":"C2:C6","conditional":{"when":">","value":1000,"style":{"fill":"verde claro"}}},{"range":"C2:C6","conditional":{"when":"<","value":1000,"style":{"color":"vermelho"}}}]""", Uso.Ops),
        new("Entre dois valores (value e value2):",
            """[{"range":"C2:C6","conditional":{"when":"between","value":500,"value2":1500,"style":{"fill":"amarelo claro"}}}]""", Uso.Ops),
        new("Texto que contém:", """[{"range":"B2:B6","conditional":{"when":"contains","value":"TI","style":{"fill":"azul claro"}}}]""", Uso.Ops),
        new("Repetidos (sem value):", """[{"range":"A2:A6","conditional":{"when":"duplicates","style":{"fill":"vermelho claro"}}}]""", Uso.Ops),
        new("Escala de cores:", """[{"range":"C2:C6","conditional":{"scale":"verde-amarelo-vermelho"}}]""", Uso.Ops),
        new("Barra de dados:", """[{"range":"C2:C6","conditional":{"bar":"azul"}}]""", Uso.Ops),
        new("Ícones:", """[{"range":"C2:C6","conditional":{"icons":"semaforo"}}]""", Uso.Ops),
        new("Tirar as regras de uma faixa e pôr outra, num pedido só:",
            """[{"clear_conditional":"C2:C6"},{"range":"C2:C6","conditional":{"when":">=","value":1500,"style":{"bold":true}}}]""", Uso.Ops),
    };

    private static readonly Exemplo[] DeAbas =
    {
        new("Criar aba (vai para o fim):", """[{"add_sheet":"Painel"}]""", Uso.Ops),
        new("Criar aba já como a primeira:", """[{"add_sheet":"Painel","position":1}]""", Uso.Ops),
        new("Renomear:", """[{"rename_sheet":"Resumo","to":"Resumo 2026"}]""", Uso.Ops),
        new("Mudar de lugar (1 = primeira):", """[{"move_sheet":"Resumo","position":1}]""", Uso.Ops),
        new("Criar, escrever e formatar a aba nova no mesmo pedido:",
            """[{"add_sheet":"Painel","position":1},{"sheet":"Painel","cell":"A1","value":"PAINEL","style":{"bold":true,"size":16}}]""", Uso.Ops),
        new("Congelar só a linha 1 (o cabeçalho):", """[{"freeze":"A2"}]""", Uso.Ops),
        new("Congelar a linha 1 e a coluna A:", """[{"freeze":"B2"}]""", Uso.Ops),
        new("Descongelar:", """[{"freeze":"none"}]""", Uso.Ops),
        new("Largura ajustada ao texto:", """[{"columns":"A:D","width":"auto"}]""", Uso.Ops),
        new("Largura fixa (em caracteres; o padrão do Excel é 8,43):", """[{"columns":"B","width":25}]""", Uso.Ops),
    };

    private static readonly Exemplo[] DeLayout =
    {
        new("Título de painel: uma linha nova no topo, alta, mesclada na largura da tabela, letra grande:",
            """[{"insert_rows":1,"rows":[""]},{"rows":"1","height":40},{"merge":"A1:D1"},{"cell":"A1","value":"VENDAS 2026","style":{"bold":true,"size":20,"valign":"middle","indent":1}}]""", Uso.Ops),
        new("Linha fina vazia separando blocos (insere antes da 7 e baixa a altura):",
            """[{"insert_rows":7,"rows":[""]},{"rows":"7","height":8}]""", Uso.Ops),
        new("Altura volta a se ajustar ao texto:", """[{"rows":"2:6","height":"auto"}]""", Uso.Ops),
        new("Esconder as linhas de grade (a folha fica branca, só a sua tabela aparece):", """[{"gridlines":false}]""", Uso.Ops),
        new("Lista suspensa: a célula só aceita estes valores:",
            """[{"range":"F2:F6","validation":{"list":["pendente","pago","cancelado"]}}]""", Uso.Ops),
        new("Lista suspensa com os valores de uma faixa da planilha:", """[{"range":"F2:F6","validation":{"list":"=$B$2:$B$6"}}]""", Uso.Ops),
        new("Tirar a lista suspensa:", """[{"range":"C2:C6","validation":["a","b"]},{"range":"C2:C6","validation":"none"}]""", Uso.Ops),
        new("Filtro (setas no cabeçalho) na tabela inteira, cabeçalho incluído:", """[{"filter":"A1:D6"}]""", Uso.Ops),
        new("Impressão: deitada, todas as colunas numa página de largura, repetindo o cabeçalho:",
            """[{"print":{"orientation":"landscape","fit":"width","repeat_rows":"1","margins":"estreita"}}]""", Uso.Ops),
        new("Imprimir só uma faixa, centralizada:", """[{"print":{"area":"A1:D6","center":true}}]""", Uso.Ops),
        new("Desfazer uma mescla:", """[{"merge":"A8:D8"},{"unmerge":"A8:D8"}]""", Uso.Ops),
    };

    private static readonly Exemplo[] DeConsulta =
    {
        new("Somar uma coluna (col = cabeçalho ou letra):", """{"agg":[{"fn":"sum","col":"Valor"}]}""", Uso.Consulta),
        new("Total e quantidade por setor, do maior para o menor:",
            """{"group":"Setor","agg":[{"fn":"sum","col":"Valor"},{"fn":"count"}],"sort":"-sum(Valor)"}""", Uso.Consulta),
        new("Filtrar linhas (sem agg, lista as linhas que passam):",
            """{"where":[{"col":"Setor","op":"=","value":"TI"},{"col":"Valor","op":">","value":1000}],"limit":20}""", Uso.Consulta),
        new("Data a partir de um dia, contando (agg também aceita texto 'count' ou 'sum(Valor)'):",
            """{"where":[{"col":"Data","op":">=","value":"2026-10-01"}],"agg":["count","sum(Valor)"]}""", Uso.Consulta),
        new("Texto que contém, por letra de coluna:", """{"where":[{"col":"A","op":"contains","value":"note"}]}""", Uso.Consulta),
        new("Só as linhas visíveis (como o SUBTOTAL, com filtro do Excel ligado):",
            """{"agg":[{"fn":"sum","col":"Valor"}],"visible":true}""", Uso.Consulta),
    };

    private static readonly Exemplo[] DeCriar =
    {
        new("Planilha nova (action='create', path terminando em .xlsx ou .csv); a linha 1 sai em negrito:", Vendas, Uso.CriarPlanilha),
        new("Word novo (path terminando em .docx):", Proposta, Uso.CriarWord),
    };

    private static readonly Exemplo[] DoWord =
    {
        new("Trocar um trecho (comparação exata; o trecho tem de ser único):", """[{"replace":"30 dias","with":"45 dias"}]""", Uso.OpsDoWord),
        new("Trocar TODAS as ocorrências:", """[{"replace":"Proposta","with":"Oferta","all":true}]""", Uso.OpsDoWord),
        new("Parágrafo novo depois do ¶1 (style: Heading1, Heading2, Heading3, List ou Normal):",
            """[{"insert_after":1,"text":"Resumo da oferta","style":"Heading2"}]""", Uso.OpsDoWord),
        new("Apagar o ¶2:", """[{"delete":2}]""", Uso.OpsDoWord),
        new("Célula de tabela (tabela 1, linha 2, coluna 2; contam a partir de 1):",
            """[{"table":1,"row":2,"col":2,"value":"R$ 120"}]""", Uso.OpsDoWord),
    };

    // ─────────────────────────────────────────────────────────────────────────
    // Os assuntos
    // ─────────────────────────────────────────────────────────────────────────

    private static readonly Assunto[] Todos =
    {
        new("ler", new[] { "info", "read", "find", "leitura", "acoes", "ações", "geral" },
            "info, read e find: abrir, ler trechos e achar texto", Ler, Array.Empty<Exemplo>()),
        new("consulta", new[] { "query", "contas", "somar" },
            "query: somar, contar, média, agrupar e filtrar sem fazer conta de cabeça", Consulta, DeConsulta),
        new("celulas", new[] { "células", "celula", "célula", "valores", "formulas", "fórmulas", "linhas", "rows", "cell", "range", "edit", "editar" },
            "edit: valores, fórmulas, faixas, inserir e apagar linhas", Celulas, DeCelulas),
        new("estilo", new[] { "style", "formatacao", "formatação", "formatar", "cores", "cor", "number_format", "formato", "bordas", "border" },
            "style: negrito, cores, fundo, bordas, alinhamento, formato de número", Estilo, DeEstilo),
        new("condicional", new[] { "conditional", "clear_conditional", "regras" },
            "conditional: pintar conforme o valor, escalas, barras e ícones", Condicional, DeCondicional),
        new("abas", new[] { "aba", "sheet", "sheets", "add_sheet", "rename_sheet", "move_sheet", "freeze", "congelar", "columns", "colunas", "largura" },
            "abas (criar, renomear, mover), congelar painel e largura de coluna", Abas, DeAbas),
        new("layout", new[] { "acabamento", "painel", "altura", "height", "mesclar", "merge", "unmerge", "grade", "gridlines",
                              "lista", "validation", "validacao", "validação", "filtro", "filter", "impressao", "impressão", "print", "imprimir" },
            "acabamento: altura de linha, mesclar, grade, lista suspensa, filtro e impressão", Layout, DeLayout),
        new("criar", new[] { "create", "content", "novo" },
            "create: planilha ou Word novos do zero", Criar, DeCriar),
        new("word", new[] { "docx", "documento", "pdf" },
            "Word e PDF: ler por ¶, trocar texto, inserir, apagar, tabela", Word, DoWord),
    };

    /// <summary>Os nomes dos assuntos, na ordem do índice.</summary>
    public static IReadOnlyList<string> Assuntos => Todos.Select(a => a.Nome).ToList();

    /// <summary>Todos os exemplos, com o assunto de cada um (para os testes).</summary>
    public static IEnumerable<(string Assunto, Exemplo Exemplo)> Exemplos =>
        Todos.SelectMany(a => a.Exemplos.Select(e => (a.Nome, e)));

    /// <summary>O manual de um assunto; sem assunto (ou com um que não existe), o índice.</summary>
    public static string Texto(string? pedido)
    {
        string p = (pedido ?? "").Trim().ToLowerInvariant();
        if (p is "tudo" or "all" or "todos")
            return string.Join("\n\n", Todos.Select(Montar));

        var achado = Todos.FirstOrDefault(a => a.Nome == p || a.Apelidos.Contains(p));
        if (achado != null) return Montar(achado);

        var sb = new StringBuilder();
        if (p.Length > 0) sb.Append("Não há o assunto \"").Append(pedido!.Trim()).Append("\". ");
        sb.AppendLine("MANUAL DA FERRAMENTA doc. Peça um assunto: action='help', topic='<assunto>' (ou topic='tudo' para o manual inteiro).");
        sb.AppendLine();
        foreach (var a in Todos) sb.Append("- ").Append(a.Nome).Append(": ").AppendLine(a.Resumo);
        sb.AppendLine();
        sb.Append(Gerais);
        return sb.ToString().TrimEnd();
    }

    private static string Montar(Assunto a)
    {
        var sb = new StringBuilder();
        sb.Append("## ").Append(a.Nome).Append(" — ").AppendLine(a.Resumo);
        sb.AppendLine(a.Texto().Trim());
        if (a.Exemplos.Length > 0)
        {
            sb.AppendLine();
            sb.AppendLine(a.Exemplos[0].Uso switch
            {
                Uso.Consulta => "Exemplos (o objeto 'query' de action='query'):",
                Uso.CriarPlanilha or Uso.CriarWord => "Exemplos (o objeto 'content' de action='create'):",
                _ => "Exemplos (a lista 'ops' de action='edit'):"
            });
            foreach (var e in a.Exemplos)
                sb.Append("- ").AppendLine(e.Legenda).Append("  ").AppendLine(Compacto(e.Json));
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>O JSON numa linha só, sem a indentação do código.</summary>
    private static string Compacto(string json) =>
        string.Join("", json.Split('\n').Select(l => l.Trim()));

    // ─────────────────────────────────────────────────────────────────────────
    // Os textos
    // ─────────────────────────────────────────────────────────────────────────

    private const string Gerais = """
        Regras gerais:
        - Planilha: .xlsx, .xlsm, .xls e .csv. Word: .docx. PDF. Lê todos; grava .xlsx, .xlsm, .csv e .docx (.xls e PDF só se leem).
        - 'path' é sempre o caminho absoluto do arquivo.
        - Gravar (create, edit) mostra ao usuário um cartão com o antes e o depois, e só grava se ele permitir.
          O original vai para uma cópia de segurança antes (as 5 últimas versões, por 3 meses).
        - Um 'edit' é tudo ou nada: se UMA operação da lista tem erro, nada é gravado, e o erro diz qual
          (operação N, contando de 1). Corrija aquela e mande a lista de novo.
        - .xlsx com gráfico, tabela dinâmica, macro ou segmentação é recusado: regravar apagaria essas partes.
          Ofereça criar um arquivo novo com o resultado.
        - Arquivo aberto no Excel ou no Word: o usuário escolhe na hora entre fechar e tentar de novo, salvar
          uma cópia ou desistir.
        - Não use o shell nem o código-fonte do AIB para planilha e Word: o que esta ferramenta faz está neste manual.
        """;

    private static string Ler() => """
        Comece por action='info': diz as abas (ou os ¶ e páginas), o tamanho e o cabeçalho de cada aba.

        action='read' traz um trecho. Planilha: cada linha vem com o número dela e as colunas com a letra;
        fórmula aparece como ⟨=…⟩ ao lado do valor; linha oculta vem marcada "(oculta)".
        - 'sheet': a aba (sem ela, a primeira visível).
        - 'range': 'A1:F200' (faixa), '10:60' (linhas inteiras), 'B:D' (colunas), 'B5' (uma célula), ou nada (a aba toda).
        - Trecho grande para no limite e diz como continuar ("continue com range='61:200'").
        Word: 'range' em ¶ — '12-40' ou '3'. PDF: páginas — '2-4'.

        action='find' com 'text' acha o texto em TODAS as abas, sem diferenciar maiúsculas nem acentos, e também
        dentro das fórmulas. Devolve a célula (Aba!B12) e a linha inteira em volta. Word e PDF: devolve o ¶.

        Para contar, somar, achar maior e menor ou agrupar, não leia tudo: use action='query' (topic='consulta').

        Exemplos de chamada:
        - {"action":"info","path":"C:\\Pasta\\vendas.xlsx"}
        - {"action":"read","path":"C:\\Pasta\\vendas.xlsx","sheet":"Vendas","range":"A1:D50"}
        - {"action":"find","path":"C:\\Pasta\\vendas.xlsx","text":"notebook"}
        """ + "\n\n" + Gerais;

    private static string Consulta() => $$$"""
        action='query' faz a conta no arquivo e devolve o resultado. Só planilha. 'sheet' escolhe a aba (fora ou
        dentro do objeto 'query'); sem ela, a primeira. A linha de cabeçalho é achada sozinha.
        Todas as chaves são opcionais:
        - where: lista de filtros {col, op, value}; todos precisam valer. op: = != > < >= <= contains.
          value é texto, número ou true/false. Número em texto vale ("1.234,56" ou "1234.56"); data em texto
          também ("2026-10-01" ou "01/10/2026").
        - group: a coluna pela qual agrupar.
        - agg: lista de contas {fn, col}. fn: {{{string.Join(", ", ConsultaDePlanilha.Funcoes)}}} (média = avg).
          count sem col conta linhas. Também aceita o texto: "sum(Valor)", "count".
        - sort: a coluna ou o rótulo da conta, como aparece no resultado ("sum(Valor)"); "-" na frente = decrescente.
        - limit: quantas linhas ou grupos mostrar (padrão {{{ConsultaDePlanilha.LimitePadrao}}}, máximo {{{ConsultaDePlanilha.LimiteMaximo}}}).
        - visible: true conta só as linhas visíveis (com filtro do Excel ligado, como o SUBTOTAL).
        col é o texto do cabeçalho ("Valor") ou a letra da coluna ("C").
        Com group, o resultado traz uma linha por grupo e o TOTAL. Sem group e sem agg, lista as linhas que passam no where.

        Chamada completa:
        {"action":"query","path":"C:\\Pasta\\vendas.xlsx","sheet":"Vendas","query":{"group":"Setor","agg":[{"fn":"sum","col":"Valor"}]}}
        """;

    private static string Celulas() => """
        action='edit' com 'ops': uma LISTA de operações, aplicadas em ordem e gravadas de uma vez.
        Cada objeto da lista é UMA operação. 'sheet' (em cada operação, ou no topo do pedido) escolhe a aba;
        sem ela, a primeira visível.

        Escrever:
        - {cell:'B2', value} — uma célula.
        - {range:'D2:D50', value} — o mesmo valor em todas as células da faixa. Em fórmula, {row} vira o número
          da linha de cada célula ("=B{row}*2" → =B2*2, =B3*2…). 'range' precisa das duas pontas ('D2:D50');
          linhas ou colunas inteiras ('2:5', 'D:D') não servem aqui.
        - value: número JSON (1234.56, sem aspas: "1234,56" entre aspas fica TEXTO); texto; true/false;
          data como texto 'dd/mm/aaaa' ou 'aaaa-mm-dd', com ou sem hora ('dd/mm/aaaa hh:mm'), que vira data do
          Excel (dá para somar, filtrar e ordenar); null apaga o conteúdo
          (a formatação fica).
        - Fórmula: texto começando com '='. Funções com o nome em INGLÊS e argumentos separados por VÍRGULA,
          como no arquivo do Excel: SUM, IF, VLOOKUP, COUNTIF, SUMIF, AVERAGE, ROUND, IFERROR, TEXT…
          (=SOMA(...) ou ponto e vírgula não funcionam). Outra aba: =Vendas!C2 ou ='Aba com espaço'!C2.
          Depois de gravar, o cartão e o resultado mostram o valor que cada fórmula passou a dar e as fórmulas
          afetadas pela mudança.

        Linhas:
        - {insert_rows:N, rows:[[...],[...]]} — insere as linhas ANTES da linha N; o que estava em N desce.
          rows é uma lista de linhas, cada linha uma lista de valores na ordem das colunas (A, B, C…).
          Uma lista solta (["a",1]) é uma linha só.
        - {delete_rows:'18:19'} (ou '18-19', ou 18) — apaga as linhas; as de baixo sobem. Fórmula que apontava
          SÓ para a linha apagada vira #REF!, como no Excel; faixa que a incluía (SUM(B2:B40)) encolhe.

        Até 100.000 células por pedido.
        Para formatar, veja topic='estilo'; formatação condicional, topic='condicional'; abas, congelar e
        largura, topic='abas'.
        """;

    private static string Estilo()
    {
        string cores = string.Join(", ", EstiloDePlanilha.Cores.Select(c => $"{c.Key} ({c.Value})"));
        string atalhos = string.Join(", ", EstiloDePlanilha.AtalhosDeFormato.Select(a => $"'{a.Key}' = {a.Value}"));
        return $$$"""
            'style' vai junto de cell ou range, com ou sem value: {cell:'A1', style:{...}} ou {range:'A1:D1', value:'x', style:{...}}.
            Na faixa, vale para todas as células. Só muda as chaves que você mandar; o resto da formatação fica.
            Chaves aceitas: {{{string.Join(", ", EstiloDePlanilha.Chaves)}}}. Outra chave é recusada.

            - bold, italic, underline: true ou false.
            - color: a cor da LETRA. fill: a cor do FUNDO da célula.
            - size: tamanho da letra, número (11 é o padrão). font: nome da fonte ("Arial", "Calibri", "Segoe UI").
            - align: left, center ou right. valign: top, middle (ou center) ou bottom.
            - wrap: true quebra o texto em várias linhas dentro da célula.
            - indent: recuo do texto, de 0 a 15 (1 basta). Com align left, afasta o texto da borda.
            - border: um texto ou uma lista deles, aplicados em ordem: {{{string.Join(", ", EstiloDePlanilha.TiposDeBorda)}}}.
              all (= thin): todas as bordas, inclusive as de dentro da faixa. outline: só o contorno da faixa.
              thick: contorno grosso. top, bottom, left, right: um lado. double: linha dupla embaixo (de total).
              none: tira todas. Ex.: ["outline","double"].
            - number_format: um atalho — {{{atalhos}}} —
              ou qualquer código de formato do Excel, escrito como no Excel em inglês: vírgula = milhar,
              ponto = decimal ("#,##0.00", "0.0%", "dd/mm/yyyy hh:mm", "[Red]-#,##0"). O Excel do usuário mostra
              com o separador do Brasil.

            Cores: um nome desta lista ou "#RRGGBB" (qualquer cor):
            {{{cores}}}.
            Fora da lista, use o hex: verde-água "#20B2AA", azul petróleo "#1F4E79", grafite "#333F4F".
            """;
    }

    private static string Condicional() => $$$"""
        Formatação condicional: o Excel pinta a célula conforme o valor, e continua pintando quando o valor muda.
        Vai numa operação de range: {range:'C2:C50', conditional:{...}}. Um destes quatro tipos:

        1. Regra com estilo: {when, value, value2?, style}
           - when: > < >= <= = != (comparam número; com texto, comparam o texto), between (precisa de value e
             value2), contains (o texto contém value), duplicates e unique (repetidos ou únicos; sem value).
           - style aqui aceita só: bold, italic, color (letra) e fill (fundo). Cores como em topic='estilo'.
        2. Escala de cores: {scale:'...'} — {{{string.Join(", ", CondicionalDePlanilha.Escalas)}}}.
           A primeira cor vai no MENOR valor ("verde-vermelho": menor verde, maior vermelho).
        3. Barra de dados: {bar:'cor'} — nome ou #RRGGBB.
        4. Ícones: {icons:'...'} — {{{string.Join(", ", CondicionalDePlanilha.Icones)}}}.

        - Várias regras na mesma faixa (verde acima de 1000 E vermelho abaixo de 0): uma operação para cada,
          no MESMO pedido; todas ficam.
        - Uma regra nova na mesma faixa (exata) de uma que o arquivo JÁ tinha substitui a antiga. Para trocar
          todas as regras de uma faixa, comece o pedido com clear_conditional e crie as novas em seguida.
        - {clear_conditional:'C2:C50'} tira todas as regras que tocam a faixa (erro se não houver nenhuma).
        - O cartão mostra quantas células cada regra pinta hoje.
        - CSV não guarda formatação: ofereça salvar uma cópia em .xlsx.
        """;

    private static string Abas() => """
        Abas (cada uma é uma operação do 'ops' de action='edit'):
        - {add_sheet:'Nome'} cria no fim; {add_sheet:'Nome', position:1} cria já na posição (1 = primeira).
          Também vale {add_sheet:true, sheet:'Nome'}.
        - {rename_sheet:'Velho', to:'Novo'} (ou {rename_sheet:true, sheet:'Velho', to:'Novo'}).
        - {move_sheet:'Nome', position:N} — N de 1 ao número de abas.
        - Nome de aba: até 31 caracteres, sem : \ / ? * [ ], sem apóstrofo no começo ou no fim, sem repetir
          outro (maiúsculas não contam).
        - A aba criada já pode receber valores e estilo nas operações seguintes do MESMO pedido (com 'sheet').
        - Apagar aba não existe nesta ferramenta: peça ao usuário.

        Congelar painel: {freeze:'célula'} — a célula é a PRIMEIRA que rola; o que fica acima e à esquerda dela
        fica parado.
        - 'A2': só a linha 1 (o cabeçalho). 'B1': só a coluna A. 'B2': a linha 1 e a coluna A.
          'C3': as linhas 1-2 e as colunas A-B. 'none': descongela.

        Largura de coluna: {columns:'A' ou 'A:F', width:N | 'auto'}
        - N em caracteres (o padrão do Excel é 8,43; 15 a 25 cabe um nome, 40+ uma descrição).
        - 'auto' ajusta ao maior texto; acima de 60 trava em 60 e liga a quebra de texto.
        Altura de linha, mesclar, grade, lista suspensa, filtro e impressão: topic='layout'.
        Gráfico não existe nesta ferramenta.
        """;

    private static string Layout() => """
        Cada item é uma operação do 'ops' de action='edit'; 'sheet' escolhe a aba, como sempre.

        - {rows:'1' ou '2:20', height:N | 'auto'} — altura da linha em pontos: 15 é o padrão; 30 a 50 um título;
          8 a 11 uma linha vazia fina que separa blocos; 'auto' volta a ajustar ao texto.
        - {merge:'A1:F1'} — junta a faixa numa célula só (título, rótulo de bloco). O Excel mostra só a
          PRIMEIRA célula: se outra tiver conteúdo, a mescla é recusada (apague-a antes com value null).
          Escreva o texto e o estilo na primeira célula (A1). {unmerge:'A1:F1'} desfaz.
        - {gridlines:false} — esconde as linhas de grade da aba; true mostra de novo.
        - {range:'F2:F50', validation:{list:['a','b','c']}} — lista suspensa: a célula só aceita esses valores.
          Valores de uma faixa: {list:'=$H$2:$H$9'}. Lista solta também vale: validation:['a','b'].
          Item com vírgula ou aspas: ponha os itens numa faixa. validation:'none' tira a lista.
        - {filter:'A1:O26'} — setas de filtro no cabeçalho: a faixa é a tabela inteira, com o cabeçalho na
          primeira linha. {filter:'none'} tira. Para contar só o que o filtro mostra, query com visible:true.
        - {print:{...}} — impressão da aba; mande só as chaves que quer mudar:
          orientation 'portrait' (em pé) ou 'landscape' (deitada); fit 'width' (todas as colunas numa página
          de largura), 'page' (tudo numa página) ou 'none'; area 'A1:F40' ou 'none'; margins 'estreita',
          'normal', 'larga' ou um número em centímetros; repeat_rows '1' (repete o cabeçalho em cada
          página); center true (centraliza na largura).

        Acabamento que faz uma planilha parecer profissional (em vez de uma grade com cor):
        - Grade escondida, e borda SÓ onde há tabela. Linha vazia entre blocos sem borda e mais baixa (8 a 11).
        - Coluna A estreita (width 2 a 4) como margem; as colunas da tabela com larguras pelo conteúdo,
          não todas iguais.
        - Título numa linha alta (40 a 50), mesclado na largura da tabela, letra 18 a 22.
        - valign 'middle' e indent 1 nas células de texto; números à direita ou centralizados.
        - Poucas cores: uma escura para cabeçalhos (letra branca), uma clara para destaque, o resto branco.
          Fonte única na planilha toda.
        - Tabela de dados: cabeçalho congelado (freeze) e filtro; coluna de status com lista suspensa e
          condicional pintando cada valor.
        - Impressão ajustada à largura antes de entregar.
        """;

    private static string Criar() => """
        action='create' com 'path' (o arquivo novo, ou um existente para SUBSTITUIR inteiro) e 'content'.

        Planilha (.xlsx ou .csv): {sheets:[{name, rows:[[...],[...]]}]}
        - Uma entrada em sheets por aba; rows são as linhas a partir da A1, cada uma uma lista de valores.
        - Valores como no edit (topic='celulas'): número JSON, texto, true/false, data 'dd/mm/aaaa',
          fórmula começando com '=' (em inglês, com vírgula).
        - A linha 1 de cada aba sai em negrito e as colunas se ajustam ao texto. Para mais formatação
          (cores, moeda, congelar), crie e depois faça um 'edit' no mesmo arquivo.
        - .csv tem uma aba só.

        Word (.docx): {blocks:[...]}, um bloco por item, em ordem:
        - {style, text}: parágrafo. style: Heading1, Heading2, Heading3 ou Normal (sem style, Normal).
        - {list:['item', 'item']}: lista com marcadores.
        - {table:[['Cab 1','Cab 2'],['a','b']]}: tabela; a primeira linha é o cabeçalho.
        """;

    private static string Word() => """
        Ler: action='info' dá o número de ¶ (parágrafos, itens de lista e tabelas contam como blocos) e páginas;
        action='read' com range '12-40' traz os ¶ numerados; action='find' acha o texto e diz o ¶.
        PDF: só leitura, range em páginas ('2-4'). 'query' não existe para Word e PDF.

        Editar .docx: action='edit' com 'ops'. A edição entra como CONTROLE DE ALTERAÇÕES, assinada com o seu
        nome: o usuário aceita ou rejeita cada uma no Word. Nada se perde.
        - {replace:'trecho exato', with:'novo'} — a comparação é exata (maiúsculas, espaços, pontuação). Se o
          trecho aparece mais de uma vez, ponha mais texto em volta ou use all:true. Na dúvida, action='find'
          mostra o trecho como está escrito.
        - {insert_after:¶, text, style} — parágrafo novo depois do ¶ (número do read). style: Heading1,
          Heading2, Heading3, List ou Normal.
        - {delete:¶} — apaga o parágrafo.
        - {table:K, row:R, col:C, value} — troca o texto de uma célula de tabela (K-ésima tabela; tudo conta de 1).
        Os números de ¶ são os do read de ANTES do pedido, mesmo com várias operações na lista.
        """;
}
