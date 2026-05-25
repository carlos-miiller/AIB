# Regras de Código Limpo e Arquitetura (AIB)

Este documento dita as normas imutáveis de engenharia de software do projeto AIB. O objetivo não é apenas que o código funcione, mas que permaneça absurdamente leve, isolado e elegante. Nenhuma *"gambiarra"* será tolerada em futuras manutenções.

## 1. Composição em Raiz (Sem Container de DI)
Diferente da maioria dos tutoriais genéricos de C# moderno, o AIB **NÃO utiliza** o pesado `Microsoft.Extensions.DependencyInjection` (IServiceCollection).
- **A Regra:** A classe `ChatWindow.xaml.cs` é o nosso *Composition Root*. 
- **O Motivo:** Isso garante ciclos de vida de objetos explícitos e atrelados estritamente à janela, evitando *Memory Leaks* em um aplicativo que passa 99% do dia dormindo (idle) na bandeja do sistema. Os serviços base devem ser instanciados com o `new` logo na inicialização da janela.

## 2. Padrão de Separação de Serviços (SOLID)
O cérebro do projeto mora na pasta `Services/`. Não polua o code-behind do WPF (o arquivo `.xaml.cs`) com lógicas cognitivas pesadas!
- **Responsabilidade Única (SRP):** Cada integração sistêmica possui uma classe autônoma.
  - Precisou bater em uma API Web? Crie um arquivo (Ex: `WebSearchService.cs`).
  - Precisou interagir com o Microfone? Crie a sua classe isolada (`VoiceService.cs`).
- O Code-behind do WPF deve lidar exclusivamente com a interface do usuário (Cliques, Eventos de Teclado, Renderização XAML).

## 3. O Padrão `ITool` Obrigatório
Para uma Inteligência Artificial, ferramentas são membros virtuais.
- **Interface Base:** Se você quer que o LLM consiga "fazer" alguma coisa no PC do usuário, você DEVE criar uma classe dentro de `NativeTools.cs` (ou num arquivo de skill isolado) implementando a interface `ITool`.
- **Contrato Estrito:** O LLM só entende Texto e JSON. Portanto, o método `ExecuteAsync(string jsonArgs)` de toda ferramenta deve devolver uma simples `string` limpa.
- **Tratamento de Exceções:** **Nunca deixe uma Tool explodir o aplicativo.** Se a ferramenta falhar, faça um `try/catch` e retorne a string de erro em português para a IA ler: `"Erro ao ler o arquivo: Acesso negado."`. O ReAct Loop fará o LLM entender a falha e pedir desculpas ao humano sem dar Crash no WPF.

## 4. O Pesadelo do UI Thread (Async / Await / Dispatcher)
O AIB é um aplicativo Gráfico no Windows, o que significa que o `UI Thread` é rei.
- Lógicas que envolvam chamadas de rede (OpenAI), cálculos no banco de dados (LiteDB) ou varreduras no disco (Leitura de PDF) **jamais** devem ser síncronas. Use `await Task.Run()` ou bibliotecas inerentemente assíncronas.
- Ao atualizar a interface vindo de um processamento pesado, utilize **SEMPRE** o `Dispatcher.Invoke(() => { ... })`. Modificar elementos XAML fora do Dispatcher resultará numa Exceção instantânea (*InvalidOperationException: The calling thread cannot access this object*).

## 5. Resolução de Conflitos e Aliases
O projeto integra muitas bibliotecas pesadas de baixo nível, causando conflitos naturais de nomes de classes (Namespaces).
- **Cores Místicas:** Cuidado com classes que existem tanto em `System.Drawing` (usado por PDFs e WinForms) quanto em `System.Windows.Media` (WPF puro). Nas declarações de topo (`using`), use Aliases (`using WColor = System.Windows.Media.Color;`) para cegar o compilador do erro CS0104.
- **Área de Transferência:** Utilize o caminho completo `System.Windows.Clipboard` em detrimento de invocar apenas `Clipboard`, para não bater de frente com a versão morta do `.Forms`.
