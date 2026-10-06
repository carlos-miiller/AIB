using System.Collections.Generic;
using System.Linq;
using AIB.Services;
using AIB.Services.Agent;
using AIB.Services.Tools;
using FluentAssertions;
using Xunit;

namespace AIB.Tests
{
    /// <summary>
    /// O filtro da saída de comando e a guarda de releitura. Os dois existem por números medidos
    /// nas sessões gravadas: a saída de shell somava 204.849 tokens×turnos, e três leituras do
    /// mesmo HTML no mesmo turno, 93 mil.
    /// </summary>
    [Collection("ContextoGlobal")]
    public class FiltroDeSaidaTests : System.IDisposable
    {
        private readonly bool _antes = FiltroDeSaida.Ligado;

        public FiltroDeSaidaTests() => FiltroDeSaida.Ligado = true;

        public void Dispose() => FiltroDeSaida.Ligado = _antes;

        [Fact]
        public void LinhaRepetida_ViraUmaComContagem_EDizOQueFez()
        {
            string saida = "Aguardando o banco...\n" + string.Join("\n", Enumerable.Repeat("Waiting for db", 7))
                           + "\npronto";

            string r = FiltroDeSaida.Aplicar(saida, RunCommandTool.TetoDaSaida);

            r.Should().Contain("Waiting for db  (×7)");
            r.Split('\n').Count(l => l.StartsWith("Waiting for db")).Should().Be(1);
            r.Should().Contain("[saída filtrada:").And.Contain("6 repetida(s) colapsada(s)");
        }

        [Fact]
        public void SaidaCurtaSemRepeticao_SaiIgual()
        {
            FiltroDeSaida.Aplicar("uma\nduas\ntrês", RunCommandTool.TetoDaSaida).Should().Be("uma\nduas\ntrês");
        }

        [Fact]
        public void SaidaLonga_GuardaOComecoOErroDoMeioEOFim()
        {
            // O corte antigo era cego: os primeiros 8.000 caracteres. Num log de build o erro
            // mora no meio ou no fim, e era o que ficava de fora.
            var linhas = new List<string> { "PRIMEIRA LINHA" };
            linhas.AddRange(Enumerable.Range(0, 300).Select(i => $"compilando módulo {i} de 600"));
            linhas.Add("error: falta o ponto e vírgula em x.cs");
            linhas.AddRange(Enumerable.Range(300, 300).Select(i => $"compilando módulo {i} de 600"));
            linhas.Add("ULTIMA LINHA");

            string r = FiltroDeSaida.Aplicar(string.Join("\n", linhas), RunCommandTool.TetoDaSaida);

            r.Should().StartWith("PRIMEIRA LINHA", "é na primeira linha que a memória lê o que aconteceu");
            r.Should().Contain("error: falta o ponto e vírgula em x.cs");
            r.Should().Contain("ULTIMA LINHA");
            r.Should().Contain("do meio omitida(s)");
            r.Length.Should().BeLessThan(FiltroDeSaida.Teto + 400);
        }

        [Fact]
        public void Desligado_VoltaOCorteAntigo()
        {
            FiltroDeSaida.Ligado = false;
            string saida = string.Join("\n", Enumerable.Repeat("Waiting for db", 3));

            FiltroDeSaida.Aplicar(saida, RunCommandTool.TetoDaSaida).Should().Be(saida);
            FiltroDeSaida.Aplicar(new string('x', 9000), 8000).Should().EndWith("[Saída truncada devido ao tamanho máximo].");
        }

        [Fact]
        public void OErroDoShell_ContinuaNaPrimeiraPosicao()
        {
            // O "ERRO" na primeira palavra é o que a memória, a tela e o recado reconhecem.
            string saida = "linha\n" + string.Join("\n", Enumerable.Repeat("repetida", 5));
            RunCommandTool.Montar(saida, "", 2).Should().StartWith("ERRO (código de saída 2)");
        }

        // ─────────────────────────────────────────────────────────────────────
        // Guarda de releitura
        // ─────────────────────────────────────────────────────────────────────

        private static readonly string Html = "     1\t<div>" + new string('x', 2000) + "</div>";
        private const string Args = "{\"path\":\"C:\\\\x\\\\assinatura.html\"}";

        [Fact]
        public void ReleituraIdenticaNoTurno_VaiComoAviso_ENaoComoOTextoDeNovo()
        {
            var jaLidas = new HashSet<string>();

            AgentLoop.GuardaDeReleitura(Ferramentas.Ler, Args, Html, jaLidas).Should().BeNull("a primeira vai inteira");

            string? aviso = AgentLoop.GuardaDeReleitura(Ferramentas.Ler, Args, Html, jaLidas);
            aviso.Should().NotBeNull();
            aviso!.Should().StartWith("[já lido neste turno]").And.NotStartWith("ERRO",
                "não é falha: o conteúdo está no contexto");
            aviso.Length.Should().BeLessThan(Html.Length / 3);
        }

        [Fact]
        public void ArquivoQueMudou_OuOutraFaixa_VaiInteiro()
        {
            var jaLidas = new HashSet<string>();
            AgentLoop.GuardaDeReleitura(Ferramentas.Ler, Args, Html, jaLidas);

            AgentLoop.GuardaDeReleitura(Ferramentas.Ler, Args, Html + "\n     2\tnova", jaLidas)
                .Should().BeNull("texto diferente é leitura nova");
        }

        [Fact]
        public void TurnoNovo_ZeraAGuarda()
        {
            // O conjunto nasce a cada turno: entre turnos, resultados antigos são escondidos e a
            // compactação tira turnos do contexto, e o aviso apontaria para um texto que sumiu.
            AgentLoop.GuardaDeReleitura(Ferramentas.Ler, Args, Html, new HashSet<string>());
            AgentLoop.GuardaDeReleitura(Ferramentas.Ler, Args, Html, new HashSet<string>()).Should().BeNull();
        }

        [Fact]
        public void SoVale_ParaLeituraQueDeuCerto_ELonga()
        {
            var jaLidas = new HashSet<string>();
            string erro = "ERRO: 'C:\\x' não existe." + new string(' ', 700);

            AgentLoop.GuardaDeReleitura(Ferramentas.Ler, Args, erro, jaLidas);
            AgentLoop.GuardaDeReleitura(Ferramentas.Ler, Args, erro, jaLidas).Should().BeNull("falha não é conteúdo");

            AgentLoop.GuardaDeReleitura(Ferramentas.Shell, "{}", Html, jaLidas);
            AgentLoop.GuardaDeReleitura(Ferramentas.Shell, "{}", Html, jaLidas).Should().BeNull("só o read");

            AgentLoop.GuardaDeReleitura(Ferramentas.Ler, Args, "curto", jaLidas);
            AgentLoop.GuardaDeReleitura(Ferramentas.Ler, Args, "curto", jaLidas).Should().BeNull("aviso custaria o mesmo");
        }
    }
}
