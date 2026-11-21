	bool DeleteRectangle(int pane, int left, int top, int right, int bottom)
	{
		if (pane < 0 || pane >= m_nImages || m_bRO[pane])
			return false;

		Image *oldbitmap = new Image(m_imgOrig32[pane]);

		{
			TemporaryTransformation tmp(*this);
			for (unsigned i = top; i < static_cast<unsigned>(bottom); ++i)
			{
				unsigned char* scanline = m_imgOrig32[pane].scanLine(i);
				memset(scanline + left * 4, 0, (right - left) * 4);
			}
		}

		Image *newbitmap = new Image(m_imgOrig32[pane]);
		m_undoRecords.push_back(pane, oldbitmap, newbitmap);

		CompareImages();
		return true;
	}
