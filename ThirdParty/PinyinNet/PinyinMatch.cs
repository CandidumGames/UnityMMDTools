using System;

namespace PinyinNet
{
	/// <summary>
	/// 通过指定输入判断匹配的位置, 判断支持 仅声母或者声母韵母
	/// 函数支持以下情况:  昆明市五华区 -> kunmingshiwuhuaqu, kmingshiwhq, kmswhq
	/// </summary>
	public class PinyinMatch
	{
		private readonly string m_strText;
		private readonly string m_strPinyin;

		public PinyinMatch(string strText)
		{
			m_strText = (strText ?? "").ToLower();
			m_strPinyin = PinyinConvert.GetPinyin(m_strText).ToLower();
		}


		/// <summary>
		/// 等待匹配的文本
		/// </summary>
		public string Text => m_strText;

        public string PinyinText => m_strPinyin;

        /// <summary>
		/// 判断是否匹配指定的字符串, 或者对应的拼音, 如果匹配了, 返回开始处的索引, 否则返回 -1
		/// </summary>
		/// <param name="strInput"></param>
		/// <returns></returns>
		public int IndexOf(string strInput)
		{
			strInput = strInput.ToLower();

			if (string.IsNullOrEmpty(strInput))
			{
				return 0;
			}

			int index = m_strText.IndexOf(strInput, StringComparison.Ordinal);
			if (index < 0)
			{
				int length = 0;
				if (!Match(strInput, ref index, ref length))
				{
					index = -1;
				}
			}

			return index;
		}


		/// <summary>
		/// 判断输入部分匹配了多少个字
		/// </summary>
		/// <param name="strInput"></param>
		/// <returns></returns>
		public int MatchLength(string strInput)
		{
			strInput = strInput.ToLower();

			if (string.IsNullOrEmpty(strInput))
			{
				return 0;
			}

			if (string.IsNullOrEmpty(m_strText))
			{
				return 0;
			}

			if (m_strText.Contains(strInput))
			{
				return strInput.Length;
			}

			int iStart = 0;
			int iLength = 0;
			if (!Match(strInput, ref iStart, ref iLength))
			{
				return 0;
			}

			return iLength;
		}


        /// <summary>
        /// 判断是否匹配了拼音
        /// 并返回匹配的部分
        /// </summary>
        /// <param name="strInput"></param>
        /// <param name="iStart"></param>
        /// <param name="iLength"></param>
        /// <returns></returns>
        private bool Match(string strInput, ref int iStart, ref int iLength)
		{
			int pinyinIndex = 0;
			int pinyinWordIndex = 0;
			bool bMatch = false;

			while (pinyinIndex < m_strPinyin.Length)
			{
				// 用户输入的第一个字母, 看看可以从哪一个拼音开始匹配
				bMatch = false;
				int pinyinTestIndex = pinyinIndex;
				int pinyinTestWordIndex = pinyinWordIndex;	// 匹配了多少个字
				int pinyinTestThisWordOffset = pinyinTestIndex;	// 当前字的开始位置
				if (strInput[0] == m_strPinyin[pinyinTestIndex])	// 第一个字母匹配成功, 开始测试后续字母是否可以陆续匹配
				{
					bMatch = true;
					pinyinTestIndex += 1;
					for (int inputIndex = 1; inputIndex < strInput.Length; inputIndex++)
					{
						// 尝试到末尾没有成功匹配
						if (pinyinTestIndex >= m_strPinyin.Length)
						{
							bMatch = false;
							break;
						}

						if (strInput[inputIndex] != m_strPinyin[pinyinTestIndex])
						{
							// 匹配不上, 跳到下一个字母, 要求仅仅匹配了拼音首字母, 例如 昆明 , kum 因为昆字没有完整输入, 却输入了一半,这样是不允许跳转的, 直接匹配失败
							// 如果完整匹配, 那么虽然第一个条件大于1, 但是第二个条件肯定匹配, 因为完整匹配后遇到的一定是竖线
							if (pinyinTestIndex - pinyinTestThisWordOffset > 1 && m_strPinyin[pinyinTestIndex] != '|')
							{
								bMatch = false;
								break;
							}

							pinyinTestThisWordOffset =  pinyinTestIndex = m_strPinyin.IndexOf("|", pinyinTestIndex, StringComparison.Ordinal) + 1;
							pinyinTestWordIndex      += 1;
							if (pinyinTestIndex <= 0 || pinyinTestIndex >= m_strPinyin.Length)
							{
								bMatch = false;
								break;
							}

							// 到这里, 已经跳到了下一个字的声母处, 如果依然匹配失败, 可以退出了
							if (strInput[inputIndex] != m_strPinyin[pinyinTestIndex])
							{
								bMatch = false;
								break;
							}
						}

						pinyinTestIndex += 1;
					}

					// pinyinTestIndex 保存了匹配到哪一个字
				}

				// 匹配成功, 退出
				if (bMatch)
				{
					iStart = pinyinWordIndex;
					iLength = pinyinTestWordIndex - pinyinWordIndex + 1;
					break;
				}

				// 从当前字的声母匹配失败, 跳到下一个字的拼音声母位置
				pinyinIndex     =  m_strPinyin.IndexOf("|", pinyinIndex, StringComparison.Ordinal) + 1;
				pinyinWordIndex += 1;

				// 越界退出
				if (pinyinIndex <= 0)
				{
					break;
				}
			}

			return bMatch;
		}

		public override string ToString()
		{
			return string.Format("{0} - {1}", Text, PinyinText);
		}
	};
}
