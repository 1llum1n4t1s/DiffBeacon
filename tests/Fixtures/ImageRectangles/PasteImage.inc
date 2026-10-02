	void PasteImage(int pane, int x, int y, const Image& image)
	{
		if (pane < 0 || pane >= m_nImages)
			return;

		Image *oldbitmap = new Image(m_imgOrig32[pane]);
		{
			TemporaryTransformation tmp(*this);
			PasteImageInternal(pane, x, y, image);
		}
		Image *newbitmap = new Image(m_imgOrig32[pane]);
		m_undoRecords.push_back(pane, oldbitmap, newbitmap);
		CompareImages();
	}
