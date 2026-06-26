using System.Collections.Generic;

namespace PinyinNet
{
    /// <summary>
    /// 拼音树节点
    /// </summary>
    class PinyinTreeNode
    {
        /// <summary>
        /// 构造函数，对于没有拼音的节点，默认构造
        /// </summary>
        public PinyinTreeNode()
        {
            Pinyin     = string.Empty;
            PinyinWord = string.Empty;
            Nodes      = new Dictionary<char, PinyinTreeNode>();
        }


        /// <summary>
        /// 多个字的拼音使用竖线隔开，如果节点不存在拼音，则为string.Empty
        /// </summary>
        public string Pinyin
        {
            get;
            set;
        }


        /// <summary>
        /// 如果这个节点拥有拼音的话，保存拼音代表的字或者词
        /// </summary>
        public string PinyinWord
        {
            get;
            set;
        }


        /// <summary>
        /// 拼音树下级节点
        /// </summary>
        public Dictionary<char, PinyinTreeNode> Nodes
        {
            get;
            private set;
        }


        /// <summary>
        /// 转换为字符串
        /// </summary>
        /// <returns></returns>
        public override string ToString()
        {
            var str = Pinyin == string.Empty ?
                $"<无拼音节点>, 子节点{Nodes.Count}个" :
                $"{PinyinWord}({Pinyin}), 子节点{Nodes.Count}个";
            return str;
        }
    };
}
