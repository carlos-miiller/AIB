using System;
using System.IO;
using System.Linq;
using AIB.Services;
using FluentAssertions;
using Xunit;

namespace AIB.Tests
{
    /// <summary>
    /// A persona puxando assunto pelo orbe, por sorteio com multiplicadores. Pedido: "no lugar de
    /// usarmos num de falas por dia, um multiplicador de chances dela falar que roda de tempos em
    /// tempos, e quando a porcentagem acertar, dispara para o modelo pensar se quer falar".
    /// </summary>
    public class IniciativaTests : IDisposable
    {
        private static readonly TimeSpan Dez = TimeSpan.FromHours(10);
        private static readonly TimeSpan Ini = TimeSpan.FromHours(22);
        private static readonly TimeSpan Fim = TimeSpan.FromHours(8);
        private static readonly DateTime Agora = new(2026, 10, 6, 13, 0, 0, DateTimeKind.Utc);

        private readonly string _raiz = Path.Combine(Path.GetTempPath(), "aib-iniciativa-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            try { Directory.Delete(_raiz, true); } catch { }
        }

        private static string? Impede(EstadoDaIniciativa e, TimeSpan? hora = null, bool presente = true,
                                      bool livre = true, DateTime? conversa = null) =>
            Iniciativa.Impedimento(e, Agora, hora ?? Dez, Ini, Fim, presente, livre, conversa);

        // ── Silêncio e travas ──────────────────────────────────────────

        [Theory]
        [InlineData(23, true)]
        [InlineData(3, true)]
        [InlineData(8, false)]
        [InlineData(14, false)]
        public void OSilencio_AtravessaAMeiaNoite(int hora, bool emSilencio)
        {
            Iniciativa.EmSilencio(TimeSpan.FromHours(hora), Ini, Fim).Should().Be(emSilencio);
        }

        [Fact]
        public void SilencioComInicioIgualAoFim_EstaDesligado()
        {
            Iniciativa.EmSilencio(TimeSpan.FromHours(3), Dez, Dez).Should().BeFalse();
        }

        [Fact]
        public void ComTudoLivre_Sorteia()
        {
            Impede(new EstadoDaIniciativa()).Should().BeNull();
        }

        [Fact]
        public void NaoInsiste_EnquantoAUltimaEsperaResposta()
        {
            Impede(new EstadoDaIniciativa { FalaUtc = Agora.AddHours(-1) }).Should().Be("esperando resposta");
            Impede(new EstadoDaIniciativa { FalaUtc = Agora.AddHours(-1), RespostaUtc = Agora.AddMinutes(-50) })
                .Should().BeNull("respondida, mesmo ainda sem classificar");
        }

        [Fact]
        public void NoSilencio_AusenteOuConversando_NaoSorteia()
        {
            var e = new EstadoDaIniciativa();
            Impede(e, hora: TimeSpan.FromHours(23)).Should().Be("silêncio");
            Impede(e, presente: false).Should().Be("ausente ou ocupado");
            Impede(e, livre: false).Should().Be("turno ou conversa aberta");
            Impede(e, conversa: Agora.AddMinutes(-5)).Should().Be("conversa recente");
        }

        [Fact]
        public void SorteiaDeDezEmDezMinutos_ETemTetoDeMensagens()
        {
            Impede(new EstadoDaIniciativa { SorteioUtc = Agora.AddMinutes(-4) }).Should().Be("sorteou há pouco");
            Impede(new EstadoDaIniciativa { SorteioUtc = Agora.AddMinutes(-10) }).Should().BeNull();
            Impede(new EstadoDaIniciativa { MensagensHoje = Iniciativa.PadraoDeMensagensPorDia }).Should().Be("teto de mensagens");
        }

        [Fact]
        public void OTeto_EhOQueOUsuarioEscolheu()
        {
            // Pedido: "faz isso ser configurável" — o teto é a trava de custo, e o dinheiro é dele.
            var e = new EstadoDaIniciativa { MensagensHoje = 6 };
            Iniciativa.Impedimento(e, Agora, Dez, Ini, Fim, true, true, null, tetoDoDia: 10).Should().BeNull();
            Iniciativa.Impedimento(e, Agora, Dez, Ini, Fim, true, true, null, tetoDoDia: 3).Should().Be("teto de mensagens");

            new UserAppSettings().MensagensPorDia.Should().Be(6);
            new UserAppSettings { MensagensPorDia = 99 }.Sanear().MensagensPorDia.Should().Be(20);
            new UserAppSettings { MensagensPorDia = 0 }.Sanear().MensagensPorDia.Should().Be(1);
        }

        [Fact]
        public void DepoisDeAgoraNao_FicaEmPausa()
        {
            Impede(new EstadoDaIniciativa { PausaAteUtc = Agora.AddHours(1) }).Should().Be("pausa pedida");
        }

        // ── Sorteio ───────────────────────────────────────────────────

        [Fact]
        public void AChance_EBaseVezesGeralVezesFaixa()
        {
            var e = new EstadoDaIniciativa { Geral = 2 };
            e.Faixas[Iniciativa.FaixaDe(Dez)] = 1.5;

            Iniciativa.Chance(e, Dez).Should().BeApproximately(0.015 * 2 * 1.5, 1e-9);
        }

        [Fact]
        public void OSorteio_AcertaAbaixoDaChance()
        {
            var i = new Iniciativa(_raiz);
            i.Sortear(Agora, Dez, dado: 0.01).Should().BeTrue();
            i.Sortear(Agora, Dez, dado: 0.5).Should().BeFalse();
            i.Estado.SorteioUtc.Should().Be(Agora);
        }

        // ── Fatores ───────────────────────────────────────────────────

        [Theory]
        [InlineData(2, 1.10)]
        [InlineData(3, 1.15)]
        [InlineData(8, 1.40)]
        [InlineData(14, 1.70)]
        [InlineData(40, 1.70)]
        public void Conversa_ComecaEm110_MaisCincoCentesimosPorTurno_AteO170(int turnos, double fator)
        {
            // Decisão do usuário: a alma dela é afetuosa, e quem conversa muito com ela a deixa
            // mais inclinada a puxar assunto.
            Iniciativa.Fator(false, true, turnos, 3, false).Should().BeApproximately(fator, 1e-9);
        }

        [Theory]
        [InlineData(false, true, 1, 15, false, 1.10, "resposta única longa")]
        [InlineData(false, true, 1, 2, false, 1.05, "resposta curta")]
        [InlineData(false, false, 0, 0, true, 0.90, "leu e não respondeu")]
        [InlineData(false, false, 0, 0, false, 0.75, "nem viu")]
        [InlineData(true, true, 1, 2, true, 0.50, "agora não")]
        public void OsOutrosFatores(bool recusou, bool respondeu, int turnos, int palavras, bool leu, double fator, string caso)
        {
            Iniciativa.Fator(recusou, respondeu, turnos, palavras, leu).Should().BeApproximately(fator, 1e-9, caso);
        }

        [Theory]
        [InlineData("agora não, tô ocupado", true)]
        [InlineData("Agora nao", true)]
        [InlineData("para de me mandar isso", true)]
        [InlineData("resolvi sim, era o certificado", false)]
        [InlineData("depois eu te conto como foi", false)]
        public void Recusa(string texto, bool recusa)
        {
            Iniciativa.EhRecusa(texto).Should().Be(recusa);
        }

        // ── Aprendizado ────────────────────────────────────────────────

        private Iniciativa FalouAs10()
        {
            var i = new Iniciativa(_raiz);
            i.Falou(Agora, Dez, "Conseguiu testar o backup?", null);
            return i;
        }

        [Fact]
        public void Conversa_SobeAFaixaInteira_EOGeralPelaMetade()
        {
            var i = FalouAs10();
            i.UsuarioFalou(Agora.AddMinutes(1), "oi! resolvi sim");
            for (int t = 0; t < 3; t++) i.UsuarioFalou(Agora.AddMinutes(5 + t), "e mais uma coisa");

            i.Classificar(Agora.AddMinutes(20));
            i.Estado.FalaUtc.Should().NotBeNull("a janela de 30 min ainda está aberta");

            i.Classificar(Agora.AddMinutes(32));
            i.Estado.FalaUtc.Should().BeNull();

            double fator = 1.10 + 0.05 * 2; // 4 turnos
            i.Estado.Faixas[Iniciativa.FaixaDe(Dez)].Should().BeApproximately(fator, 1e-9);
            i.Estado.Geral.Should().BeApproximately(Math.Sqrt(fator), 1e-9);
            i.Estado.Faixas[0].Should().Be(1, "só a faixa em que ela falou aprende");
        }

        // Pedido: "que tal nós mandarmos mensagem no shadow e isso também contabilizar no
        // algoritmo?". Antes, mensagem dele no orbe sem fala dela esperando era descartada.
        [Fact]
        public void ElePuxarConversaNoOrbe_SobeAFaixaEOGeral_MenosQueResponderAEla()
        {
            var i = new Iniciativa(_raiz);
            for (int t = 0; t < 4; t++) i.UsuarioFalou(Agora.AddMinutes(t), "e outra coisa", Dez);

            i.Classificar(Agora.AddMinutes(20));
            i.Estado.ConversaUtc.Should().NotBeNull("a janela de 30 min ainda está aberta");

            i.Classificar(Agora.AddMinutes(30));
            i.Estado.ConversaUtc.Should().BeNull();

            double fator = 1.05 + 0.02 * 2; // 4 turnos
            i.Estado.Faixas[Iniciativa.FaixaDe(Dez)].Should().BeApproximately(fator, 1e-9);
            i.Estado.Geral.Should().BeApproximately(Math.Sqrt(fator), 1e-9);
            i.Estado.UltimoDesfecho.Should().BeEmpty("o desfecho é das iniciativas dela");
            fator.Should().BeLessThan(Iniciativa.Fator(false, true, 4, 0, true));
        }

        [Theory]
        [InlineData(1, 1.02)]
        [InlineData(2, 1.05)]
        [InlineData(5, 1.11)]
        [InlineData(40, 1.20)]
        public void ConversaEspontanea_SoSobe_AteO120(int turnos, double fator) =>
            Iniciativa.FatorEspontaneo(turnos).Should().BeApproximately(fator, 1e-9);

        // "para de rodar o build" numa mensagem que não responde a ela é trabalho, não recusa.
        [Fact]
        public void ConversaEspontanea_NaoViraRecusaNemPausa()
        {
            var i = new Iniciativa(_raiz);
            i.UsuarioFalou(Agora, "para de rodar o build", Dez);
            i.Classificar(Agora.AddMinutes(31));

            i.Estado.PausaAteUtc.Should().BeNull();
            i.Estado.Faixas[Iniciativa.FaixaDe(Dez)].Should().BeApproximately(1.02, 1e-9);
        }

        [Fact]
        public void MensagemDepoisDaJanela_FechaAConversaEAbreOutra()
        {
            var i = new Iniciativa(_raiz);
            i.UsuarioFalou(Agora, "oi", Dez);
            i.UsuarioFalou(Agora.AddMinutes(45), "voltei", Dez);

            i.Estado.ConversaUtc.Should().Be(Agora.AddMinutes(45));
            i.Estado.TurnosDaConversa.Should().Be(1);
            i.Estado.Faixas[Iniciativa.FaixaDe(Dez)].Should().BeApproximately(1.02, 1e-9, "a primeira já contou");
        }

        [Fact]
        public void RespostaAFalaDela_NaoContaComoConversaEspontanea()
        {
            var i = FalouAs10();
            i.UsuarioFalou(Agora.AddMinutes(1), "oi! resolvi sim", Dez);
            i.Estado.ConversaUtc.Should().BeNull();
        }

        [Fact]
        public void Ignorada_DepoisDaPaciencia_Desce()
        {
            var i = FalouAs10();
            i.Classificar(Agora.AddHours(7));
            i.Estado.FalaUtc.Should().NotBeNull();

            i.Classificar(Agora + Iniciativa.Paciencia);
            i.Estado.Faixas[Iniciativa.FaixaDe(Dez)].Should().BeApproximately(0.75, 1e-9);
        }

        [Fact]
        public void LeuENaoRespondeu_DesceMenos()
        {
            var i = FalouAs10();
            i.Leu();
            i.Classificar(Agora + Iniciativa.Paciencia);
            i.Estado.Faixas[Iniciativa.FaixaDe(Dez)].Should().BeApproximately(0.9, 1e-9);
        }

        [Fact]
        public void AgoraNao_DesceNaHora_EPausa()
        {
            var i = FalouAs10();
            i.UsuarioFalou(Agora.AddMinutes(2), "agora não");

            i.Estado.FalaUtc.Should().BeNull("classificada na hora");
            i.Estado.Faixas[Iniciativa.FaixaDe(Dez)].Should().BeApproximately(0.5, 1e-9);
            i.Estado.PausaAteUtc.Should().NotBeNull();
            (i.Estado.PausaAteUtc!.Value - Agora.AddMinutes(2)).Should().BeLessThanOrEqualTo(Iniciativa.PausaDaRecusa);
        }

        [Fact]
        public void OsMultiplicadores_TemPisoETeto()
        {
            var i = new Iniciativa(_raiz);
            for (int n = 0; n < 20; n++)
            {
                i.Falou(Agora, Dez, "Conseguiu testar o backup?", null);
                i.Classificar(Agora + Iniciativa.Paciencia);
            }
            i.Estado.Faixas[Iniciativa.FaixaDe(Dez)].Should().Be(Iniciativa.Minimo, "nunca zero: ela ainda tenta, raramente, e pode reaprender");
        }

        [Fact]
        public void ACadaDia_EsqueceDezPorCento()
        {
            Iniciativa.Esquecer(2.0).Should().BeApproximately(Math.Pow(2.0, 0.9), 1e-9);
            Iniciativa.Esquecer(0.5).Should().BeApproximately(Math.Pow(0.5, 0.9), 1e-9);

            var i = new Iniciativa(_raiz);
            i.NovoDia(new DateTime(2026, 10, 6, 9, 0, 0));
            i.Estado.Geral = 2;
            i.NovoDia(new DateTime(2026, 10, 7, 9, 0, 0));
            i.Estado.Geral.Should().BeApproximately(Math.Pow(2.0, 0.9), 1e-9);
            i.Estado.MensagensHoje.Should().Be(0);
        }

        // ── Por personagem ─────────────────────────────────────────────

        // Pedido: "separarmos essas mudanças comportamentais e intensidade de iniciativa por
        // personagem". Era um arquivo só: trocar a Ellen por outro herdava o ritmo dela.
        [Fact]
        public void OVinculo_EhDeCadaPersonagem_EAsFaixasSaoDoUsuario()
        {
            var i = new Iniciativa(_raiz, "Ellen");
            i.Falou(Agora, Dez, "Conseguiu testar o backup?", null);
            i.UsuarioFalou(Agora.AddMinutes(1), "oi! resolvi sim");
            i.UsuarioFalou(Agora.AddMinutes(2), "e mais uma coisa");
            i.Classificar(Agora.AddMinutes(32));

            double geralDaEllen = i.Estado.Geral;
            double faixa = i.Estado.Faixas[Iniciativa.FaixaDe(Dez)];
            geralDaEllen.Should().BeGreaterThan(1);
            i.Estado.UltimoDesfecho.Should().NotBeEmpty();

            i.Trocar("Kai");
            i.Estado.Geral.Should().Be(1, "o Kai nunca conversou");
            i.Estado.UltimoDesfecho.Should().BeEmpty();
            i.Estado.Faixas[Iniciativa.FaixaDe(Dez)].Should().Be(faixa, "o horário bom é do usuário, não do personagem");

            i.Trocar("Ellen");
            i.Estado.Geral.Should().Be(geralDaEllen);

            // E sobrevive ao arranque, cada um no seu arquivo.
            new Iniciativa(_raiz, "Ellen").Estado.Geral.Should().Be(geralDaEllen);
            new Iniciativa(_raiz, "Kai").Estado.Geral.Should().Be(1);
            File.Exists(Path.Combine(_raiz, "character", "Ellen", "vinculo.dat")).Should().BeTrue();
        }

        [Fact]
        public void OArquivoUnicoDeAntes_ViraOVinculoDoPersonagemAtivo_ESaiDoTextoClaro()
        {
            // O iniciativa.json em texto claro guardava tudo. No primeiro arranque o que ele
            // aprendeu fica com o personagem ativo, e o arquivo é regravado cifrado.
            Directory.CreateDirectory(_raiz);
            var antigo = new EstadoDaIniciativa { Geral = 1.4, UltimoDesfecho = "virou conversa" };
            antigo.Faixas[5] = 1.3;
            File.WriteAllText(Path.Combine(_raiz, "iniciativa.json"), System.Text.Json.JsonSerializer.Serialize(antigo));

            var i = new Iniciativa(_raiz, "Ellen");

            i.Estado.Geral.Should().Be(1.4);
            i.Estado.Faixas[5].Should().Be(1.3);
            File.Exists(Path.Combine(_raiz, "iniciativa.json")).Should().BeFalse("o texto claro não fica para trás");

            foreach (string arquivo in new[] { "iniciativa.dat", Path.Combine("character", "Ellen", "vinculo.dat") })
                System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(_raiz, arquivo)))
                    .Should().NotContain("Geral", "o arquivo é cifrado");

            // Só o ativo herda: o seguinte começa do zero.
            new Iniciativa(_raiz, "Kai").Estado.Geral.Should().Be(1);
        }

        [Fact]
        public void OTemperamento_MudaAChance_EOTetoDaConversa()
        {
            var e = new EstadoDaIniciativa();
            Iniciativa.Chance(e, Dez, 0.5).Should().BeApproximately(Iniciativa.ChanceBase * 0.5, 1e-12);

            // Dez turnos: 1,50 no padrão, mas um personagem mais seco para em 1,30.
            Iniciativa.Fator(false, true, 10, 0, true).Should().BeApproximately(1.50, 1e-9);
            Iniciativa.Fator(false, true, 10, 0, true, teto: 1.30).Should().BeApproximately(1.30, 1e-9);
            Iniciativa.FatorEspontaneo(40, apego: 1.10).Should().BeApproximately(1.10, 1e-9);

            Temperamento.De(null).Should().Be(Temperamento.Padrao);

            var seco = new Iniciativa(_raiz, "Ren", new Temperamento(1, 1.30));
            seco.Falou(Agora, Dez, "E o relatório?", null);
            for (int t = 0; t < 10; t++) seco.UsuarioFalou(Agora.AddMinutes(1 + t), "mais uma");
            seco.Classificar(Agora.AddMinutes(40));
            seco.Estado.Faixas[Iniciativa.FaixaDe(Dez)].Should().BeApproximately(1.30, 1e-9);
        }

        // ── Atributos e arquivo de status ──────────────────────────────

        [Fact]
        public void OArquivoDeStatus_NasceVazio_EGanhaOPersonagemQuandoEleAparece()
        {
            // Pedido: "não vamos trazer o arquivo já com a info dos personagens preenchida, ele
            // deve ser vazio... em qualquer momento podemos introduzir outro personagem e ele ser
            // adicionado no arquivo de status".
            var status = new StatusDosPersonagens(_raiz);
            File.Exists(status.Arquivo).Should().BeFalse("ninguém vem preenchido");
            status.Arquivo.Should().Be(Path.Combine(_raiz, "memory", "shadow", "status.json"));

            status.De("Ellen").Afeto.Should().Be(0);
            File.ReadAllText(status.Arquivo).Should().Contain("\"Ellen\"").And.NotContain("Sora");

            // Editado à mão, o AIB não desfaz; e o personagem novo entra ao lado.
            File.WriteAllText(status.Arquivo, "{ \"Ellen\": { \"Iniciativa\": 3, \"Afeto\": 5, \"Resiliencia\": 2 } }");
            new StatusDosPersonagens(_raiz).De("ellen").Afeto.Should().Be(5);
            new StatusDosPersonagens(_raiz).De("Sora").Curiosidade.Should().Be(Atributos.Neutro);

            File.ReadAllText(status.Arquivo).Should().Contain("\"Sora\"");
            new StatusDosPersonagens(_raiz).De("Ellen").Afeto.Should().Be(5, "quem já estava não é mexido");
            new StatusDosPersonagens(_raiz).De("Ellen").Resiliencia.Should().Be(2);
        }

        [Fact]
        public void OPersonagemNovo_EntraComOsPadroesDoInfo_EDepoisValeOArquivo()
        {
            // Pedido: "vamos salvar no info os stats padrões; em memory/shadow ficarão os
            // modificados".
            var status = new StatusDosPersonagens(_raiz);
            var doInfo = new Atributos { Afeto = 4, Resiliencia = 2 };

            status.De("Ellen", doInfo).Afeto.Should().Be(4);

            // Modificado no arquivo de status: o info.json deixa de mandar.
            File.WriteAllText(status.Arquivo, File.ReadAllText(status.Arquivo).Replace("\"Afeto\": 4", "\"Afeto\": -3"));
            status.De("Ellen", doInfo).Afeto.Should().Be(-3);
            status.De("Ellen", doInfo).Resiliencia.Should().Be(2);
            doInfo.Afeto.Should().Be(4, "o padrão não é o mesmo objeto do registro");
        }

        [Fact]
        public void OInfoDosPersonagensDeFabrica_TrazOsCincoAtributos()
        {
            string pasta = DirectoryService.FailsafeCharactersDir()!;
            foreach (string info in Directory.GetFiles(pasta, "info.json", SearchOption.AllDirectories))
            {
                var perfil = System.Text.Json.JsonSerializer.Deserialize<AgentProfile>(File.ReadAllText(info))!;
                perfil.Atributos.Should().NotBeNull(info);
                perfil.Atributos!.Afeto.Should().BeInRange(Atributos.AfetoMinimo, Atributos.AfetoMaximo, info);
                new[] { perfil.Atributos.Iniciativa, perfil.Atributos.Resiliencia,
                        perfil.Atributos.Constancia, perfil.Atributos.Curiosidade }
                    .Should().OnlyContain(n => n >= 1 && n <= 5, info);
            }
        }

        [Fact]
        public void OArquivoDeStatusIlegivel_NaoEhSobrescrito()
        {
            // Vírgula esquecida numa edição à mão não pode custar os outros personagens.
            var status = new StatusDosPersonagens(_raiz);
            Directory.CreateDirectory(Path.GetDirectoryName(status.Arquivo)!);
            File.WriteAllText(status.Arquivo, "{ \"Ellen\": { \"Apego\": 5 ");

            status.De("Sora").Iniciativa.Should().Be(Atributos.Neutro);
            File.ReadAllText(status.Arquivo).Should().Be("{ \"Ellen\": { \"Apego\": 5 ");
        }

        [Fact]
        public void ONivelTres_EhOComportamentoDeAntes_EAsPontasMudamAConta()
        {
            // O afeto 2 é o teto 1,70 que valia para todos, nascido com a Ellen.
            Temperamento.De(new Atributos { Afeto = 2 }).Should().Be(Temperamento.Padrao);
            Temperamento.De(new Atributos { Afeto = 2, Iniciativa = 0, Constancia = 0 }).Should().Be(Temperamento.Padrao,
                "zero não é nível: vale o neutro");

            var frio = Temperamento.De(new Atributos { Iniciativa = 1, Afeto = -5, Resiliencia = 1, Constancia = 1, Curiosidade = 1 });
            var quente = Temperamento.De(new Atributos { Iniciativa = 9, Afeto = 9, Resiliencia = 5, Constancia = 5, Curiosidade = 5 });

            (frio.Chance, quente.Chance).Should().Be((0.5, 1.6));
            (frio.Apego, quente.Apego).Should().Be((1.00, 2.00), "afeto acima de 5 vale 5");
            Temperamento.De(new Atributos()).Apego.Should().Be(1.50, "o afeto neutro");
            (frio.Ignorada, quente.Ignorada).Should().Be((0.60, 0.90));
            (frio.Recusada, quente.Recusada).Should().Be((0.35, 0.70));
            (frio.Esquecimento, quente.Esquecimento).Should().Be((0.20, 0.05));
            (frio.Curiosidade, quente.Curiosidade).Should().Be((0, 0.50));
        }

        [Fact]
        public void ASaudade_CresceComOTempoSemContato_EMaisComAfetoAlto()
        {
            // Visto no uso: um dia inteiro de orbe na tela sem ela dizer nada, a 1,6% por sorteio.
            // Pedido: a saudade, e "quanto maior o afeto, ele sobe levemente mais".
            Iniciativa.Saudade(TimeSpan.FromHours(1)).Should().Be(1, "o dia normal fica como era");
            Iniciativa.Saudade(TimeSpan.FromHours(5)).Should().BeApproximately(1.5, 1e-9);
            Iniciativa.Saudade(TimeSpan.FromHours(8)).Should().BeApproximately(2, 1e-9);
            Iniciativa.Saudade(TimeSpan.FromHours(24)).Should().BeApproximately(3, 1e-9);
            Iniciativa.Saudade(TimeSpan.FromDays(9)).Should().BeApproximately(3, 1e-9, "daí não passa");

            double sora = Temperamento.De(new Atributos { Afeto = 4 }).Saudade;
            double kai = Temperamento.De(new Atributos { Afeto = -2 }).Saudade;
            (kai, Temperamento.De(new Atributos()).Saudade, sora).Should().Be((0.88, 1.0, 1.24));
            Iniciativa.Saudade(TimeSpan.FromHours(24), sora).Should().BeApproximately(3.48, 1e-9);
            Iniciativa.Saudade(TimeSpan.FromHours(24), kai).Should().BeApproximately(2.76, 1e-9);
        }

        [Fact]
        public void OSorteio_UsaASaudade_EQualquerContatoAZera()
        {
            var i = new Iniciativa(_raiz, "Ayano", Temperamento.De(new Atributos()));
            double dado = Iniciativa.ChanceBase * 2.5;

            i.Sortear(Agora, Dez, dado).Should().BeFalse("sem contato registrado, conta a partir de agora");
            i.Sortear(Agora.AddHours(7), Dez, dado).Should().BeFalse("7 h: 1,83");
            i.Sortear(Agora.AddHours(24), Dez, dado).Should().BeTrue("um dia: 3");

            // Conversa na janela não ensina nada, mas é contato.
            i.Contato(Agora.AddHours(24));
            i.Sortear(Agora.AddHours(25), Dez, dado).Should().BeFalse();

            // Mensagem no orbe e fala dela também.
            i.UsuarioFalou(Agora.AddHours(50), "oi");
            i.Estado.ContatoUtc.Should().Be(Agora.AddHours(50));
            i.Falou(Agora.AddHours(80), Dez, "E o relatório?", null);
            i.Estado.ContatoUtc.Should().Be(Agora.AddHours(80));

            // E sobrevive ao arranque, no vínculo do personagem.
            new Iniciativa(_raiz, "Ayano").Estado.ContatoUtc.Should().Be(Agora.AddHours(80));
        }

        [Fact]
        public void OJeitoDeFalar_SoMudaQuandoOAfetoSeAfastaDoDeFabrica()
        {
            // Pedido: o comportamento e o jeito de interagir mudarem, e não só a frequência.
            // Sem desvio a linha não existe: o prompt medido continua valendo.
            ConversationService.LinhaDoAfeto(null).Should().BeEmpty("personagem que ainda não está no arquivo de status");
            ConversationService.LinhaDoAfeto(0).Should().BeEmpty();
            ConversationService.LinhaDoAfeto(0.7).Should().BeEmpty();
            ConversationService.LinhaDoAfeto(-0.7).Should().BeEmpty();

            ConversationService.LinhaDoAfeto(0.75).Should().Contain("um pouco mais à vontade");
            ConversationService.LinhaDoAfeto(2).Should().Contain("mais pessoal");
            ConversationService.LinhaDoAfeto(-0.75).Should().Contain("mais direto ao ponto");
            ConversationService.LinhaDoAfeto(-2).Should().Contain("não puxe conversa");

            // Uma linha só, no bloco de contexto, sem gênero do personagem nem do usuário.
            foreach (double d in new[] { 0.75, 2, -0.75, -2 })
            {
                string linha = ConversationService.LinhaDoAfeto(d);
                linha.Should().StartWith("\n- Convivência: ").And.NotMatchRegex("expressiva|à vontade para ser|ele tem|ela tem");
                linha.LastIndexOf('\n').Should().Be(0, "é uma linha só");
            }

            // E só lê: consultar o afeto não acrescenta ninguém ao arquivo.
            var status = new StatusDosPersonagens(_raiz);
            status.AfetoDe("Ellen").Should().BeNull();
            File.Exists(status.Arquivo).Should().BeFalse();
            status.De("Ellen", new Atributos { Afeto = 2 });
            status.Mover("Ellen", 1, 2);
            status.AfetoDe("ellen").Should().Be(3);
        }

        [Fact]
        public void ComAfetoMinimo_ConversaNaoFazPuxarMaisAssunto()
        {
            // Pedido: "-5 o personagem é totalmente direto e evita interações prolongadas".
            double teto = Temperamento.TetoDoAfeto(-5);

            Iniciativa.Fator(false, true, 10, 0, true, teto).Should().Be(1.00);
            Iniciativa.Fator(false, true, 1, 30, true, teto).Should().Be(1.00, "nem a resposta longa sobe");
            Iniciativa.FatorEspontaneo(1, teto).Should().Be(1.00);
            Iniciativa.FatorEspontaneo(9, teto).Should().Be(1.00);
        }

        [Fact]
        public void OAfeto_AndaComODesfecho_ENaoPassaDaFolga()
        {
            // Decisão: o afeto de agora sobe com conversa boa e desce com recusa, devagar, e a
            // no máximo 2 pontos do de fábrica — "para a Kai nunca virar a Sora".
            var status = new StatusDosPersonagens(_raiz);
            var fabrica = new Atributos { Afeto = -2 };
            status.De("Kai", fabrica);

            var i = new Iniciativa(_raiz, "Kai", Temperamento.De(fabrica));
            i.AfetoMoveu += passo => status.Mover(i.Personagem, passo, fabrica.Afeto);

            // Uma iniciativa que virou conversa: +0,10.
            i.Falou(Agora, Dez, "E o relatório?", null);
            for (int t = 0; t < 4; t++) i.UsuarioFalou(Agora.AddMinutes(1 + t), "mais uma");
            i.Classificar(Agora.AddMinutes(40));
            status.De("Kai").Afeto.Should().BeApproximately(-1.90, 1e-9);

            // Um "agora não": -0,15.
            i.Falou(Agora.AddHours(1), Dez, "E o deploy?", null);
            i.UsuarioFalou(Agora.AddHours(1).AddMinutes(1), "agora não");
            status.De("Kai").Afeto.Should().BeApproximately(-2.05, 1e-9);

            // Conversa puxada por ele, com dois turnos: +0,05. Com um só, nada.
            i.UsuarioFalou(Agora.AddHours(2), "oi");
            i.UsuarioFalou(Agora.AddHours(2).AddMinutes(1), "tudo bem?");
            i.Classificar(Agora.AddHours(3));
            status.De("Kai").Afeto.Should().BeApproximately(-2.00, 1e-9);

            // A folga: por mais que conversem, não passa de 0; por mais que recuse, de -4.
            status.Mover("Kai", 50, fabrica.Afeto);
            status.De("Kai").Afeto.Should().Be(0);
            status.Mover("Kai", -50, fabrica.Afeto);
            status.De("Kai").Afeto.Should().Be(-4);

            // E quem tem +4 de fábrica para no 5, não no 6.
            status.De("Sora", new Atributos { Afeto = 4 });
            status.Mover("Sora", 50, 4);
            status.De("Sora").Afeto.Should().Be(5);
        }

        [Fact]
        public void AResiliencia_MudaOQuantoIgnorarERecusarPesam_SemTrocarODesfecho()
        {
            var resiliente = Temperamento.De(new Atributos { Resiliencia = 5 });

            var ignorada = new Iniciativa(_raiz, "Kai", resiliente);
            ignorada.Falou(Agora, Dez, "E o relatório?", null);
            ignorada.Classificar(Agora + Iniciativa.Paciencia);
            ignorada.Estado.Faixas[Iniciativa.FaixaDe(Dez)].Should().BeApproximately(0.90, 1e-9);
            ignorada.Estado.UltimoDesfecho.Should().Be("ficou sem resposta");

            // O "agora não" dele vale 0,70: pelo número seria "sem resposta", e ela leria errado
            // como foi a última vez.
            var recusada = new Iniciativa(Path.Combine(_raiz, "r"), "Kai", resiliente);
            recusada.Falou(Agora, Dez, "E o relatório?", null);
            recusada.UsuarioFalou(Agora.AddMinutes(1), "agora não");
            recusada.Estado.Faixas[Iniciativa.FaixaDe(Dez)].Should().BeApproximately(0.70, 1e-9);
            recusada.Estado.UltimoDesfecho.Should().Be("recebeu um \"agora não\"");
        }

        [Fact]
        public void AConstancia_MudaOEsquecimentoDoGeral_ENaoODasFaixas()
        {
            // As faixas são de quando o usuário gosta de conversa, com quem for.
            var i = new Iniciativa(_raiz, "Ayano", Temperamento.De(new Atributos { Constancia = 1 }));
            i.NovoDia(new DateTime(2026, 10, 6, 9, 0, 0));
            i.Estado.Geral = 2.0;
            i.Estado.Faixas[4] = 2.0;

            i.NovoDia(new DateTime(2026, 10, 7, 9, 0, 0));

            i.Estado.Geral.Should().BeApproximately(Math.Pow(2.0, 0.80), 1e-9);
            i.Estado.Faixas[4].Should().BeApproximately(Math.Pow(2.0, 0.90), 1e-9);
        }

        [Fact]
        public void ACuriosidade_TrocaOGanchoPorConhecerOUsuario()
        {
            var pend = new[] { "o relatório" };
            var sorte = new Random(7);

            int semGancho = Enumerable.Range(0, 400)
                .Count(_ => Iniciativa.EscolherGancho(pend, Array.Empty<string>(), Array.Empty<string>(), sorte, 0.50) == null);

            semGancho.Should().BeInRange(150, 250);
            Iniciativa.EscolherGancho(pend, Array.Empty<string>(), Array.Empty<string>(), sorte)
                .Should().NotBeNull("sem curiosidade, havendo gancho ele é usado");
        }

        [Fact]
        public void OAprendizado_SobreviveAoArranque_EZeraPeloBotao()
        {
            var i = FalouAs10();
            i.UsuarioFalou(Agora.AddMinutes(1), "agora não");

            new Iniciativa(_raiz).Estado.Faixas[Iniciativa.FaixaDe(Dez)].Should().BeApproximately(0.5, 1e-9);

            i.Zerar();
            i.Estado.Geral.Should().Be(1);
            i.Estado.Faixas.Should().OnlyContain(f => f == 1);
        }

        [Fact]
        public void OResumo_DizOQueElaAprendeu()
        {
            var e = new EstadoDaIniciativa { Geral = 1.2 };
            e.Faixas[4] = 1.5;
            e.Faixas[7] = 0.6;

            Iniciativa.Resumo(e).Should().Be("Geral 1,20 · mais à vontade das 8h às 10h · menos das 14h às 16h");
            Iniciativa.Resumo(new EstadoDaIniciativa()).Should().Contain("ainda sem preferência");
        }

        // ── O gancho ──────────────────────────────────────────────────

        [Fact]
        public void OGancho_SorteiaPendenciaEFatoComOMesmoPeso()
        {
            // Decisão do usuário: quando a mensagem chega ele talvez nem esteja lidando com a
            // pendência, então ela não vale mais que um fato.
            var pend = new[] { "testar o backup" };
            var fatos = new[] { "trabalha com TI num hospital" };
            var sorte = new Random(7);

            var tipos = Enumerable.Range(0, 400)
                .Select(_ => Iniciativa.EscolherGancho(pend, fatos, Array.Empty<string>(), sorte)!.Tipo)
                .ToList();

            tipos.Count(t => t == "pendência").Should().BeInRange(160, 240);
        }

        [Fact]
        public void OGancho_EvitaOsUsadosHaPouco_EFaltandoTudoENulo()
        {
            var fatos = new[] { "gosta de café", "mora em Curitiba" };
            Iniciativa.EscolherGancho(Array.Empty<string>(), fatos, new[] { "gosta de café" }, new Random(1))!
                .Texto.Should().Be("mora em Curitiba");

            Iniciativa.EscolherGancho(Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), new Random(1))
                .Should().BeNull("sem gancho, o pedido é para conhecê-lo melhor");
        }

        [Fact]
        public void Falou_LembraAMensagemEOGancho_ParaNaoRepetir()
        {
            var i = new Iniciativa(_raiz);
            for (int n = 0; n < Iniciativa.Memoria + 2; n++)
                i.Falou(Agora, Dez, "mensagem " + n, new Gancho("fato", "fato " + n));

            i.Estado.Recentes.Should().HaveCount(Iniciativa.Memoria).And.EndWith("mensagem " + (Iniciativa.Memoria + 1));
            i.Estado.GanchosRecentes.Should().HaveCount(Iniciativa.Memoria);
            i.Estado.MensagensHoje.Should().Be(Iniciativa.Memoria + 2);
        }

        [Fact]
        public void ODesfecho_DizComoFoiAUltima()
        {
            var i = FalouAs10();
            i.Classificar(Agora + Iniciativa.Paciencia);
            i.Estado.UltimoDesfecho.Should().Be("ficou sem resposta");
        }

        // ── O pedido ──────────────────────────────────────────────────

        private static readonly AgentProfile Ellen = new()
        {
            Name = "Ellen",
            Description = "Vinda de uma casa nobre de espadachins.",
            Personality = "Polida e calorosa.",
            SampleSpeech = "\"Ah, e... obrigada por conferir comigo.\""
        };

        private static ConversationService.ContextoDaIniciativa Contexto(
            string nome = "Carlo", bool proximos = false, string desfecho = "", params string[] recentes) =>
            new(nome, Agora.AddHours(-3), proximos, desfecho, recentes);

        [Fact]
        public void OPedido_EUmPapel_ComPersonaCurtaGanchoEFalas()
        {
            string m = ConversationService.MaterialDaIniciativa(
                null, Ellen, new Gancho("pendência", "testar o backup"), Contexto(),
                new[] { (true, "amanhã eu testo o backup"), (false, "Combinado!") }, Agora);

            m.Should().StartWith("Você é Ellen. Vinda de uma casa nobre de espadachins.")
             .And.Contain("Você está sem fazer nada. Carlo está online. Vocês conversaram pela última vez há 3 h.")
             .And.Contain("Você decide mandar uma mensagem para Carlo.")
             .And.Contain("ficou em aberto: testar o backup")
             .And.Contain("Carlo: amanhã eu testo o backup")
             .And.NotContain("NADA", "quem decide se ela fala é o sorteio")
             .And.NotContain("conversado bastante");
        }

        [Fact]
        public void ComAlma_AAlmaVaiInteira_EAPersonaCurtaFica()
        {
            // Decisão do usuário: o sorteio controla quantas vezes ela é chamada, e toda chamada
            // vira mensagem — então a alma inteira só é paga quando ela fala de fato.
            string m = ConversationService.MaterialDaIniciativa(
                "## 1. Identidade\n- Nome: Ellen Walker", Ellen, null, Contexto(), Array.Empty<(bool, string)>(), Agora);

            m.Should().StartWith("## 1. Identidade\n- Nome: Ellen Walker")
             .And.NotContain("Um exemplo do seu jeito de falar", "a persona curta é só para quem não tem alma");
        }

        [Fact]
        public void SemNome_ElaFalaComOUsuario_ESemGancho_PuxaAssuntoParaConhecer()
        {
            string m = ConversationService.MaterialDaIniciativa(
                null, Ellen, null, Contexto(nome: ""), Array.Empty<(bool, string)>(), Agora);

            m.Should().Contain("O usuário está online").And.Contain("conhecer melhor");
        }

        [Fact]
        public void OPedido_TrazProximidadeDesfechoEOQueJaDisse()
        {
            string m = ConversationService.MaterialDaIniciativa(
                null, Ellen, new Gancho("fato", "gosta de café"),
                Contexto(proximos: true, desfecho: "virou conversa", recentes: "E o café de hoje?"),
                Array.Empty<(bool, string)>(), Agora);

            m.Should().Contain("Vocês têm conversado bastante")
             .And.Contain("Sua última mensagem assim virou conversa.")
             .And.Contain("você sabe isto sobre Carlo: gosta de café")
             .And.Contain("- E o café de hoje?");
        }

        [Theory]
        [InlineData(20, "há 20 min")]
        [InlineData(180, "há 3 h")]
        [InlineData(60 * 30, "ontem")]
        [InlineData(60 * 24 * 4, "há 4 dias")]
        public void HaQuanto(int minutos, string esperado)
        {
            ConversationService.HaQuanto(Agora.AddMinutes(-minutos), Agora).Should().Be(esperado);
        }

        [Fact]
        public void NomeDoUsuario_EhSaneado()
        {
            new UserAppSettings { NomeDoUsuario = "  Carlo   Henrique " }.Sanear().NomeDoUsuario.Should().Be("Carlo Henrique");
            new UserAppSettings { NomeDoUsuario = new string('a', 80) }.Sanear().NomeDoUsuario
                .Should().HaveLength(UserAppSettings.TetoDoNome);
        }

        [Fact]
        public void HorarioDeSilencioIlegivel_VoltaAoPadrao()
        {
            var s = new UserAppSettings { SilencioInicio = "meia-noite", SilencioFim = "7:30" }.Sanear();
            s.SilencioInicio.Should().Be("22:00");
            s.SilencioFim.Should().Be("07:30");
        }
    }
}
