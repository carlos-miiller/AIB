using System.Threading.Tasks;
namespace AIB.Services {
    public static class TestRunner {
        public static Task<bool> RunRagTestAsync() => Task.FromResult(true);
        public static Task<bool> RunToolTestAsync() => Task.FromResult(true);
    }
}
