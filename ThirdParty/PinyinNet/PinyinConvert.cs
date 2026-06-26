using System;
using System.Diagnostics;
using System.Linq;
using System.Text;

namespace PinyinNet
{
    /// <summary>
    /// 根据传入的中文创建对应的拼音（支持部分多音字）
    /// </summary>
    public class PinyinConvert
    {
        #region 静态成员和函数

        /// <summary>
        /// 拼音树
        /// </summary>
        static PinyinTreeNode _dict;


        /// <summary>
        /// 加载字典（如果没有加载过）
        /// </summary>
        private static void Load()
        {
            if (_dict == null)
            {
                LoadDictionary(PinyinResource.Pinyin);
            }
        }

        public static void LoadDictionary(string dictionaryText)
        {
            if (string.IsNullOrEmpty(dictionaryText))
            {
                throw new ArgumentException("Pinyin dictionary text is required.", nameof(dictionaryText));
            }

            PinyinTreeNode dictionary = new PinyinTreeNode();
            string[] strLines = dictionaryText.Split(new[]
            {
                '\r',
                '\n'
            }, StringSplitOptions.RemoveEmptyEntries);

            // 处理字典中的每一行
            foreach (string strLine in strLines)
            {
                // 跳过注释行
                if (strLine.StartsWith("//"))
                {
                    continue;
                }

                string[] strElems = strLine.Split('\t');
                Debug.Assert(strElems.Length == 2);
                string strWord   = strElems[0]; // 字或者词
                string strPinyin = strElems[1]; // 拼音

                PinyinTreeNode node = dictionary;
                for (int i = 0; i < strWord.Length; i++)
                {
                    // 如果包含指定的节点，跳到该节点上，否则创建节点
                    if (!node.Nodes.ContainsKey(strWord[i]))
                    {
                        node.Nodes.Add(strWord[i], new PinyinTreeNode());
                    }

                    node = node.Nodes[strWord[i]];
                }

                // 更新最后这一级的内容
                node.Pinyin     = strPinyin;
                node.PinyinWord = strWord;
            }

            _dict = dictionary;
        }


        /// <summary>
        /// 把传入的字符串翻译为拼音,每个字的拼音使用'|'隔开
        /// 遇到无法识别的字，原字输出
        /// </summary>
        /// <param name="strChinese"></param>
        /// <returns></returns>
        private static string TranslateToPinyin(string strChinese)
        {
            Load();

            if(strChinese == null )
                strChinese = "";
            
            string strPinyin = string.Empty;

            int iStep = 0;
            while (iStep < strChinese.Length)
            {
                int            iExplore    = iStep;
                PinyinTreeNode effectNode  = null;
                var currentNode = _dict;
                while (iExplore < strChinese.Length &&
                       currentNode.Nodes.ContainsKey(strChinese[iExplore]))
                {
                    var node = currentNode.Nodes[strChinese[iExplore]];
                    if (node.Pinyin != string.Empty)
                    {
                        effectNode = node;
                    }

                    currentNode =  node;
                    iExplore    += 1;
                }

                // 如果匹配到拼音，则补充这段拼音
                if (effectNode != null)
                {
                    strPinyin += effectNode.Pinyin;
                    iStep     += effectNode.PinyinWord.Length;
                }
                else
                {
                    strPinyin += strChinese[iStep] + "|";
                    iStep     += 1;
                }
            }

            return strPinyin.TrimEnd('|');
        }


        /// <summary>
        /// 把传入的字符串翻译为拼音
        /// </summary>
        /// <param name="strChinese"></param>
        /// <returns></returns>
        public static string GetPinyin(string strChinese)
        {
            return TranslateToPinyin(strChinese);
        }


        /// <summary>
        /// 把传入的字符串翻译为拼音的首字母
        /// </summary>
        /// <param name="strChinese"></param>
        /// <returns></returns>
        public static string GetPinyinFirstLetter(string strChinese)
        {
            string[] strPinyins = TranslateToPinyin(strChinese).Split(new[]
            {
                '|'
            }, StringSplitOptions.RemoveEmptyEntries);
            var sb = new StringBuilder(strPinyins.Length);
            for (int i = 0; i < strPinyins.Length; i++)
            {
                sb.Append(strPinyins[i][0]);
            }

            return sb.ToString();
        }


        /// <summary>
        /// 把传入字符串拼接为拼音、拼音简写和中文
        /// </summary>
        /// <param name="strChinese"></param>
        /// <returns></returns>
        public static string GetPinyinForAutoComplete(string strChinese)
        {
            string strPinyin = TranslateToPinyin(strChinese);
            string[] strPinyins = strPinyin.Split(new[]
            {
                '|'
            }, StringSplitOptions.RemoveEmptyEntries);
            var sb = new StringBuilder();
            for (int i = 0; i < strPinyins.Length; i++)
            {
                sb.Append(strPinyins[i][0]);
            }

            sb.AppendFormat(" {0} {1}", strPinyin.Replace("|", ""), strChinese);

            // 转换大写, 去除重复
            var items = sb.ToString().ToLower().Split(new[]
            {
                ' '
            }, StringSplitOptions.RemoveEmptyEntries).Distinct().ToArray();
            var result = string.Join(" ", items);

            return result;
        }

        #endregion

        #region 实例化函数

        /// <summary>
        /// 保存原始字符串
        /// </summary>
        private readonly string m_strText;


        /// <summary>
        /// 保存每一个字符的拼音
        /// </summary>
        private readonly string m_strPinyin;


        /// <summary>
        /// 保存m_strPinyin每一个字的开始索引, 如果是开始索引，保存True，否则保存False
        /// </summary>
        private readonly bool[] m_bIsPinyinStartIndex;


        /// <summary>
        /// 保存拼音缩写
        /// </summary>
        private readonly string m_strPinyinShort;


        /// <summary>
        /// 构造函数
        /// </summary>
        /// <param name="strText"></param>
        public PinyinConvert(string strText)
        {
            Debug.Assert(strText != null);

            // 得到拼音数组
            string strPinyin = TranslateToPinyin(strText);
            string[] strPinyins = strPinyin.Split(new[]
            {
                '|'
            }, StringSplitOptions.RemoveEmptyEntries);

            // 计算拼音缩写和拼音字开始位置索引
            StringBuilder sbPinyinShort = new StringBuilder(strPinyins.Length);
            StringBuilder sbPinyin      = new StringBuilder(strPinyin.Length);
            m_bIsPinyinStartIndex = new bool[strPinyin.Length];

            for (int i = 0; i < strPinyins.Length; i++)
            {
                // 生成缩写
                sbPinyinShort.Append(strPinyins[i][0]);

                // 标记每一个字的开始位置
                m_bIsPinyinStartIndex[sbPinyin.Length] = true;

                // 生成拼音
                sbPinyin.Append(strPinyins[i]);
            }

            // 保存结果
            m_strText        = strText.ToLower();
            m_strPinyin      = sbPinyin.ToString().ToLower();
            m_strPinyinShort = sbPinyinShort.ToString().ToLower();
        }


        /// <summary>
        /// 判断子字符串出现的位置
        /// </summary>
        /// <param name="value"></param>
        /// <returns></returns>
        public int IndexOf(string value)
        {
            value = value.ToLower();

            // 1 匹配原始字符串
            int indexOf = m_strText.IndexOf(value, StringComparison.Ordinal);

            // 2 匹配拼音缩写
            if (indexOf < 0)
            {
                indexOf = m_strPinyinShort.IndexOf(value, StringComparison.Ordinal);
                // 这里就不再做判断了，拼音缩写的长度应该等于原始字符串的长度
            }

            // 3 匹配拼音
            if (indexOf < 0)
            {
                indexOf = m_strPinyin.IndexOf(value, StringComparison.Ordinal);

                // 必须出现在开头字母才有效
                if (indexOf >= 0 && indexOf < m_bIsPinyinStartIndex.Length)
                {
                    if (!m_bIsPinyinStartIndex[indexOf])
                    {
                        indexOf = -1;
                    }
                }
            }

            return indexOf;
        }

        #endregion
    };

    #region 拼音树节点对象

    #endregion
}
