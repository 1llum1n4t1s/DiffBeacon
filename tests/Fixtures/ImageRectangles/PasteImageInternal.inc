	void PasteImageInternal(int pane, int x, int y, const Image& image)
	{
		assert(m_temporarilyTransformed);
		if (pane < 0 || pane >= m_nImages)
			return;

		int width = m_imgOrig32[pane].width();
		int height = m_imgOrig32[pane].height();
		if (width == 0 || height == 0)
			return;

		int left = std::clamp(x, 0, width - 1);
		int top = std::clamp(y, 0, height - 1);
		int right = std::clamp(static_cast<int>(x + image.width()), 0, width);
		int bottom = std::clamp(static_cast<int>(y + image.height()), 0, height);
		if (right - left <= 0)
			return;
		if (bottom - top <= 0)
			return;

		for (int i = top; i < bottom; ++i)
			memcpy(m_imgOrig32[pane].scanLine(i) + left * 4, 
				image.scanLine(i - y) + (left - x) * 4, (right - left) * 4);
	}
