using System.Collections.Generic;
namespace AIB.Services {
    public class ContextFile {
        public string FilePath { get; set; }
    }
    public static class ContextService {
        public static List<ContextFile> ActiveFiles => new List<ContextFile>();
        public static List<ContextFile> RecentFiles => new List<ContextFile>();
        public static void DeleteContext(int id) { }
        public static List<ContextFile> GetContextFiles() => new List<ContextFile>();
        public static void AddFile(string path) { }
        public static void RemoveFile(ContextFile file) { }
        public static void AddRecentFile(string path) { }
    }
}
