using System;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using FluentAssertions;

namespace AIB.Tests
{
    /// <summary>
    /// Anfitrião de WPF para os ensaios de interface: UMA thread STA para o processo inteiro.
    /// <para>
    /// O WPF exige STA, e o xUnit roda em MTA. A primeira versão disto abria uma thread STA por
    /// ensaio, e falhava de forma intermitente com "Não é possível criar mais de uma instância de
    /// System.Windows.Application no mesmo AppDomain": o <see cref="Application"/> tem afinidade
    /// de thread e só pode existir uma vez, mas as classes de ensaio rodam concorrentes e cada
    /// thread nova tentava criar a sua.
    /// </para>
    /// <para>
    /// Uma trava em volta da criação não resolvia, e a razão é mais funda que a corrida: um
    /// objeto de WPF pertence à thread que o criou. Janela montada na thread de um ensaio e
    /// tocada na thread de outro é erro de afinidade, não de sincronização. Com uma thread só,
    /// dona do Application e do Dispatcher, o problema deixa de existir em vez de ficar mais
    /// raro.
    /// </para>
    /// </summary>
    internal static class WpfHost
    {
        private static readonly object Trava = new();
        private static Dispatcher? _dispatcher;

        /// <summary>
        /// A thread STA única, criada por demanda. É de fundo para não segurar o processo: se
        /// os ensaios acabarem, ela morre junto.
        /// </summary>
        private static Dispatcher ObterDispatcher()
        {
            lock (Trava)
            {
                if (_dispatcher != null) return _dispatcher;

                var pronta = new ManualResetEventSlim(false);
                Dispatcher? criado = null;

                var thread = new Thread(() =>
                {
                    // Criados AQUI, na thread que vai ser dona deles pelo resto do processo.
                    var app = Application.Current ?? new Application();

                    // Sem isto o WPF encerra o Application quando a ÚLTIMA janela fecha — e os
                    // ensaios fecham as janelas que abrem. O Dispatcher morria no meio da
                    // suíte e os ensaios seguintes eram abortados: a contagem total variava de
                    // execução para execução, o sintoma mais confuso possível.
                    // É a mesma configuração do App.xaml de produção.
                    app.ShutdownMode = ShutdownMode.OnExplicitShutdown;

                    criado = Dispatcher.CurrentDispatcher;
                    pronta.Set();

                    Dispatcher.Run();
                })
                {
                    IsBackground = true,
                    Name = "AIB.Tests.Wpf"
                };

                thread.SetApartmentState(ApartmentState.STA);
                thread.Start();

                pronta.Wait(TimeSpan.FromSeconds(30)).Should().BeTrue("a thread de WPF precisa subir");

                _dispatcher = criado;
                return _dispatcher!;
            }
        }

        /// <summary>
        /// Roda a ação na thread de WPF e propaga a falha como exceção do ensaio.
        /// </summary>
        public static void EmSta(Action acao, int segundos = 60)
        {
            var dispatcher = ObterDispatcher();

            Exception? falha = null;

            var operacao = dispatcher.InvokeAsync(() =>
            {
                try { acao(); }
                catch (Exception ex) { falha = ex; }
            });

            operacao.Task.Wait(TimeSpan.FromSeconds(segundos))
                .Should().BeTrue("a janela não pode travar ao montar");

            if (falha != null)
                throw new Xunit.Sdk.XunitException($"a janela não montou: {falha.GetType().Name}: {falha.Message}");
        }

        /// <summary>
        /// Garante os dicionários do App.xaml. Sem eles, todo StaticResource de Themes/ some e as
        /// telas nem montam. O Application em si já nasceu com a thread.
        /// </summary>
        public static void GarantirRecursos()
        {
            var app = Application.Current;
            if (app == null) return;

            // Como o App: as cores do tema e depois os estilos. O escuro é o original; AIB_UI_TEMA=Claro
            // monta a suíte no claro, para conferir as telas com AIB_UI_PNG=1.
            if (app.Resources.MergedDictionaries.Count == 0)
                AIB.Ui.Tema.Carregar(app.Resources,
                    AIB.Ui.Tema.Efetivo(Environment.GetEnvironmentVariable("AIB_UI_TEMA"), null));
        }
    }
}
