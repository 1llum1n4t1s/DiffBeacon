	// When comparing empty file and nonexistent file, `DIFFCODE::SAME` flag is set to the variable `code`, so change the flag to `DIFFCODE::DIFF`
	// Also when disabling ignore codepage option and the encodings of files are not equal, change the flag to `DIFFCODE::DIFF even if  `DIFFCODE::SAME` flag is set to the variable `code`
	if (!di.diffcode.existAll() || (!m_pCtxt->m_bIgnoreCodepage && !std::equal(encoding + 1, encoding + nDirs, encoding)))
		code = (code & ~DIFFCODE::COMPAREFLAGS) | DIFFCODE::DIFF;

