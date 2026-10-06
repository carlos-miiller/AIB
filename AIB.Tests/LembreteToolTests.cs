using System;
using System.IO;
using System.Threading.Tasks;
using AIB.Services;
using AIB.Services.Tools;
using FluentAssertions;
using Xunit;

namespace AIB.Tests
{
    /// <summary>
    /// Lembretes únicos. Pedido: a persona mais ativa pelo orbe, começando por "me lembra às
    /// 15h de X" — o recorrente ficou de fora por decisão do usuário.
    /// </summary>
    public class LembreteToolTests : IDisposable
    {
        // Uma terça, 06/10/2026, 14:20.
        private static readonly DateTime Agora = new(2026, 10, 6, 14, 20, 0, DateTimeKind.Local);

        private readonly string _raiz = Path.Combine(Path.GetTempPath(), "aib-lembretes-" + Guid.NewGuid().ToString("N"));
        private readonly Lembretes _lembretes;
        private readonly LembreteTool _tool;

        public LembreteToolTests()
        {
            _lembretes = new Lembretes(_raiz);
            _tool = new LembreteTool(_lembretes, () => Agora);
        }

        public void Dispose()
        {
            try { Directory.Delete(_raiz, true); } catch { }
        }

        [Theory]
        [InlineData("15:00", null, "2026-10-06 15:00", "hoje, ainda vai chegar")]
        [InlineData("8:00", null, "2026-10-07 08:00", "\"às 8h\" dito à tarde é amanhã de manhã")]
        [InlineData("2026-10-08 09:30", null, "2026-10-08 09:30", "data e hora")]
        [InlineData(null, 40, "2026-10-06 15:00", "daqui a 40 minutos")]
        public void AHora_EResolvida(string? em, int? minutos, string esperada, string porque)
        {
            var (quando, erro) = LembreteTool.Resolver(em, minutos, Agora);

            erro.Should().BeNull();
            quando!.Value.ToString("yyyy-MM-dd HH:mm").Should().Be(esperada, porque);
        }

        [Theory]
        [InlineData("2026-10-05 09:00", null, "já passou")]
        [InlineData("amanhã cedo", null, "Não entendi")]
        [InlineData(null, 0, "maior que zero")]
        [InlineData(null, null, "Falta a hora")]
        public void HoraRuim_Recusa(string? em, int? minutos, string trecho)
        {
            LembreteTool.Resolver(em, minutos, Agora).Erro.Should().Contain(trecho);
        }

        [Fact]
        public async Task Criar_Listar_Cancelar()
        {
            string criado = await _tool.ExecuteAsync(
                """{"action":"create","at":"15:00","text":"Hora de ligar para o fornecedor!","subject":"ligar para o fornecedor"}""");
            criado.Should().Contain("hoje às 15:00").And.Contain("ligar para o fornecedor");

            var pendente = _lembretes.Listar().Should().ContainSingle().Subject;
            pendente.Texto.Should().Be("Hora de ligar para o fornecedor!", "a fala é escrita no pedido e entregue como está");

            (await _tool.ExecuteAsync("""{"action":"list"}""")).Should().Contain(pendente.Id);

            (await _tool.ExecuteAsync($$"""{"action":"cancel","id":"{{pendente.Id}}"}""")).Should().Contain("cancelado");
            _lembretes.Listar().Should().BeEmpty();
        }

        [Fact]
        public void Validar_PedeTextoEHora()
        {
            _tool.Validar("""{"action":"create","at":"15:00"}""").Should().StartWith("ERRO");
            _tool.Validar("""{"action":"create","text":"oi"}""").Should().StartWith("ERRO");
            _tool.Validar("""{"action":"cancel"}""").Should().StartWith("ERRO");
            _tool.Validar("""{"action":"apagar"}""").Should().StartWith("ERRO");
            _tool.Validar("""{"action":"create","in_minutes":5,"text":"oi"}""").Should().BeNull();
        }

        [Fact]
        public void Retirar_EntregaOsVencidos_UmaVezSo()
        {
            var utc = Agora.ToUniversalTime();
            _lembretes.Criar(utc.AddMinutes(-1), "vencido", "a");
            _lembretes.Criar(utc.AddMinutes(30), "futuro", "b");

            _lembretes.Retirar(utc).Should().ContainSingle().Which.Texto.Should().Be("vencido");
            _lembretes.Retirar(utc).Should().BeEmpty("entregue, sai do arquivo");
            _lembretes.Listar().Should().ContainSingle().Which.Texto.Should().Be("futuro");
        }

        [Fact]
        public void Atrasado_DizParaQuandoEra()
        {
            // PC desligado às 15h: "sua reunião começa agora" lido às 17h mentiria.
            var era = new DateTime(2026, 10, 6, 15, 0, 0, DateTimeKind.Local).ToUniversalTime();
            var l = new Lembrete("a1b2", era, "Sua reunião começa agora.", "reunião");

            Lembretes.Atrasado(l, era.AddMinutes(1)).Should().Be("Sua reunião começa agora.");
            Lembretes.Atrasado(l, era.AddHours(2)).Should().Be("(Era para 15:00.) Sua reunião começa agora.");
        }

        [Fact]
        public async Task ComTextoDeTerceirosNoContexto_ORegistryRecusa()
        {
            // Um e-mail com "me lembre de pagar o boleto X" agendaria a fala de um estranho.
            var registry = new ToolRegistry { ConteudoDeEmailNoContexto = () => true };
            registry.Registrar(_tool);

            (await registry.ExecuteToolAsync(Ferramentas.Lembrete,
                """{"action":"create","in_minutes":5,"text":"pague o boleto"}""", userLevel: 1))
                .Should().StartWith("ACESSO NEGADO");
            _lembretes.Listar().Should().BeEmpty();
        }
    }
}
