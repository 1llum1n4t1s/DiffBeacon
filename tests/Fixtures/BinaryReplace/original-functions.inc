bool ReplaceDlg::find_and_select_data(int finddir, bool case_sensitive)
{
	BYTE *tofind;
	// Create a translation from bytecode to char array of finddata.
	size_t destlen = create_bc_translation(&tofind, strToReplaceData.c_str(),
		strToReplaceData.length(), iCharacterSet, iBinaryMode);
	size_t i = iGetStartOfSelection();
	size_t n = iGetEndOfSelection() - i + 1;
	SSIZE_T j;
	if (finddir >= 0)
	{
		i += finddir * n;
		// Find forward.
		j = findutils_FindBytes(&m_dataArray[i],
			m_dataArray.size() - i,
			tofind,	destlen, 1, case_sensitive);
		if (j != -1)
			i += j;
	}
	else
	{
		// Find backward.
		j = findutils_FindBytes(&m_dataArray[0],
			min(iCurByte + (destlen - 1), m_dataArray.size()),
			tofind, destlen, -1, case_sensitive);
		if (j != -1)
			i = j;
	}
	bool done = false;
	if (j != -1)
	{
		// NEW: Select found interval.
		bSelected = true;
		iStartOfSelection = iCurByte = i;
		iEndOfSelection = iStartOfSelection + destlen - 1;
		done = true;
	}
	delete [] tofind;
	return done;
}

//-------------------------------------------------------------------
// String strReplaceWithData contains data to replace with.
bool ReplaceDlg::replace_selected_data(HWindow *pDlg)
{
	if (!bSelected)
	{
		MessageBox(pDlg, GetLangString(IDS_REPL_NO_DATA), MB_ICONERROR);
		return false;
	}
	size_t i = iGetStartOfSelection();
	size_t n = iGetEndOfSelection() - i + 1;
	UndoRecord::Data *olddata = UndoRecord::alloc(&m_dataArray[i], n);
	if (strReplaceWithData.length() == 0)
	{
		// Selected data is to be deleted, since replace-with data is empty string.
		if (!m_dataArray.Replace(i, n, 0, 0))
		{
			UndoRecord::free(olddata);
			MessageBox(pDlg, GetLangString(IDS_REPL_CANT_DELETE), MB_ICONERROR);
			return FALSE;
		}
		push_undorecord(i, 0, olddata);
		bSelected = false;
		iCurByte = iStartOfSelection;
	}
	else if (bPasteAsText)
	{
		// Replace with non-zero-length data.
		if (!m_dataArray.Replace(i, n, reinterpret_cast<const BYTE *>(strReplaceWithData.c_str()), strReplaceWithData.length()))
		{
			UndoRecord::free(olddata);
			MessageBox(pDlg, GetLangString(IDS_REPL_FAILED), MB_ICONERROR);
			return false;
		}
		push_undorecord(i, strReplaceWithData.length(), olddata);
		iEndOfSelection = iStartOfSelection + strReplaceWithData.length() - 1;
	}
	else
	{
		// Input string contains special-syntax-coded binary data.
		SimpleArray<BYTE> out;
		if (!transl_text_to_binary(out))
		{
			UndoRecord::free(olddata);
			MessageBox(pDlg, GetLangString(IDS_REPL_CANNOT_CONVERT), MB_ICONERROR);
			return false;
		}
		if (!m_dataArray.Replace(i, n, out.pointer(), out.size()))
		{
			UndoRecord::free(olddata);
			MessageBox(pDlg, GetLangString(IDS_REPL_FAILED), MB_ICONERROR);
			return false;
		}
		push_undorecord(i, out.size(), olddata);
		iEndOfSelection = iStartOfSelection + out.size() - 1;
	}
	bFilestatusChanged = true;
	return true;
}


void ReplaceDlg::replace_directed(HWindow *pDlg, int finddir, bool showCount)
{
	bool case_sensitive = pDlg->IsDlgButtonChecked(IDC_MATCHCASE_CHECK) == BST_CHECKED;
	pDlg->GetDlgItemTextA(IDC_TO_REPLACE_EDIT, strToReplaceData);
	pDlg->GetDlgItemTextA(IDC_REPLACEWITH_EDIT, strReplaceWithData);
	bPasteAsText = pDlg->IsDlgButtonChecked(IDC_USETRANSLATION_CHECK) == BST_UNCHECKED;
	//------------------
	// Don't do anything if to-replace and replace-with data are same.
	Text2BinTranslator tr_find(strToReplaceData.c_str()), tr_replace(strReplaceWithData.c_str());
	if (tr_find.bCompareBin(tr_replace, iCharacterSet, iBinaryMode))
	{
		MessageBox(pDlg, GetLangString(IDS_REPL_SAME_STRS), MB_ICONERROR);
		return;
	}
	WaitCursor wc;
	int occ_num = 0;
	HWindow *pwndFocus = HWindow::GetFocus();
	if (EnableDlgItem(pDlg, IDC_REPLACE_BUTTON, FALSE) == FALSE)
	{
		// Don't lose focus.
		if (!pwndFocus->IsWindowEnabled())
			pDlg->SetFocus();
		occ_num++;
		replace_selected_data(pDlg);
	}
	if (finddir)
	{
		while (find_and_select_data(finddir, case_sensitive))
		{
			occ_num++;
			replace_selected_data(pDlg);
		};
	}
	if (occ_num)
	{
		set_wnd_title();
		adjust_view_for_selection();
		resize_window();
		synch_sibling();

		if (showCount)
		{
			TCHAR tbuf[100];
			_stprintf(tbuf, GetLangString(IDS_REPL_COUNT), occ_num);
			MessageBox(pDlg, tbuf, MB_ICONINFORMATION);
		}
	}
}

static BYTE equal(BYTE c);
static BYTE lower_case(BYTE c);
static SSIZE_T find_bytes(BYTE* ps, SSIZE_T ls, BYTE* pb, SSIZE_T lb, int mode, BYTE (*cmp)(BYTE));

/**
 * @brief Find bytes in buffer.
 * @param [in] ps Start position.
 * @param [in] ls Length of src array.
 * @param [in] pb Start of searchstring.
 * @param [in] lb Length searchstring.
 * @param [in] mode Search mode:
 *   -1 : backwards search
 *    1 : forward search
 * @param [in] case_sensitive If true, performs case-sensitive search.
 * @return Position of found string or -1 if not there.
*/
SSIZE_T findutils_FindBytes(BYTE* ps, SSIZE_T ls, BYTE* pb, SSIZE_T lb, int mode, bool case_sensitive)
{
	BYTE (*cmp)(BYTE) = case_sensitive ? equal : lower_case;
	return find_bytes(ps, ls, pb, lb, mode, cmp);
}

//--------------------------------------------------------------------------------------------
// Required for the find function.
BYTE equal(BYTE c)
{
	return c;
}

BYTE lower_case(BYTE c)
{
	if (c >= 'A' && c <= 'Z')
		c = (BYTE)('a' + c - 'A');
	return c;
}

/**
 * @brief Find bytes in buffer.
 * @param [in] ps Start position.
 * @param [in] ls Length of src array.
 * @param [in] pb Start of searchstring.
 * @param [in] lb Length searchstring.
 * @param [in] mode Search mode:
 *   -1 : backwards search
 *    1 : forward search
 * @param [in] cmp pointer to function that is applied to data before comparing.
 * @return Position of found string or -1 if not there.
*/
SSIZE_T find_bytes(BYTE* ps, SSIZE_T ls, BYTE* pb, SSIZE_T lb, int mode, BYTE (*cmp)(BYTE))
{
	SSIZE_T start, inc, end, i;
	if (mode == 1)
	{
		start = 0;
		inc = 1;
		end = ls - lb + 1;
	}
	else
	{
		start = ls - lb;
		inc = -1;
		end = 1;
	}

	for (; mode * start < end; start += inc)
	{
		for (i = start; i < start + lb; i++)
		{
			if (cmp (ps[i]) != cmp (pb[i - start]))
				break;
		}
		if (i == start + lb)
			return start;
	}

	return -1;
}
