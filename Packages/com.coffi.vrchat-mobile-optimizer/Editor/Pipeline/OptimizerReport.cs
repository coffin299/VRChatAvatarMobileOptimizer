using System.Collections.Generic;

namespace Coffi.MobileOptimizer
{
    /// <summary>処理中に削除したもの・警告・情報を集める。</summary>
    public class OptimizerReport
    {
        public readonly List<string> Warnings = new List<string>();
        public readonly List<string> Infos = new List<string>();
        public readonly SortedDictionary<string, List<string>> Removed = new SortedDictionary<string, List<string>>();
        public string Error;

        public void Warn(string message) => Warnings.Add(message);
        public void Info(string message) => Infos.Add(message);

        public void AddRemoved(string category, string item)
        {
            if (!Removed.TryGetValue(category, out var list))
            {
                list = new List<string>();
                Removed[category] = list;
            }
            list.Add(item);
        }
    }
}
