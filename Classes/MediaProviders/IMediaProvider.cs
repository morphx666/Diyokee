namespace Diyokee.MediaProviders {   
    public interface IMediaProvider {
        public class MediaFolder(string name, string relativePath, bool hasSubfolders) {
            public string Name { get; init; } = name;
            public string RelativePath { get; init; } = relativePath;
            public bool HasSubfolders { get; init; } = hasSubfolders;

            private bool isExpanded = false;
            public bool IsExpanded { 
                get => isExpanded;
                set {
                    isExpanded = value;
                    if(isExpanded) IsBusy = true;
                }
            }
            public bool IsBusy { get; set; } = false;

            public override string ToString() {
                return RelativePath;
            }
        }

        public string Name { get; }
        public string RootPath { get; }
        public string InitialPath { get; }
        public List<MediaFolder> Directories(string relativePath);
        public List<string> Files(string relativePath);
        public List<string> Search(string relativePath, string query, bool recursive);
    }
}
