struct DIFFCODE
{
	/**
	 * @brief values for DIFFITEM.diffcode
	 */
	enum : unsigned long
	{
		// We use extra bits so that no valid values are 0
		// and each set of flags is in a different hex digit
		// to make debugging easier
		// These can always be packed down in the future
		TEXTFLAGS=0x3FU, TEXT=0x1U, BIN=0x2U, BINSIDE1=0x4U, BINSIDE2=0x8U, BINSIDE3=0x10U, IMAGE=0x20U,
		TYPEFLAGS=0xC0U, FILE=0x40U, DIR=0x80U,
		COMPAREFLAGS=0x7000U, NOCMP=0x0000U, DIFF=0x1000U, SAME=0x2000U, CMPERR=0x3000U, CMPABORT=0x4000U,
		COMPAREFLAGS3WAY=0x18000U, DIFFALL=0x0000U, DIFF1STONLY=0x8000U, DIFF2NDONLY=0x10000U, DIFF3RDONLY=0x18000U,
		FILTERFLAGS=0x20000U, INCLUDED=0x00000U, SKIPPED=0x20000U,
		SCANFLAGS=0x100000U, NEEDSCAN=0x100000U,
		THREEWAYFLAGS=0x200000U, THREEWAY=0x200000U,
		EXPRFLAGS = 0x400000U, EXPRDIFF = 0x400000U,
		PATHFLAGS = 0x800000U, PATHMISMATCH = 0x800000U,
		SIDEFLAGS=0x70000000U, FIRST=0x10000000U, SECOND=0x20000000U, THIRD=0x40000000U, BOTH=0x30000000U, ALL=0x70000000U,
	};

	unsigned diffcode;

	explicit DIFFCODE(unsigned diffcode = 0) : diffcode(diffcode) { }

protected:
	/// Worker function, to check one area (mask) of code for a particular value (result)
	static bool Check(unsigned code, unsigned mask, unsigned result) { return ((code & mask) == result); }
	/// Convenience function to check the part of the code for comparison results
	static bool CheckCompare(unsigned code, unsigned result) { return Check(code, DIFFCODE::COMPAREFLAGS, result); }
	/// Convenience function to check the part of the code for filter status
	static bool CheckFilter(unsigned code, unsigned result) { return Check(code, DIFFCODE::FILTERFLAGS, result); }
	/// Convenience function to check the part of the code for side status (eg, left-only)
	static bool CheckSide(unsigned code, unsigned result) { return Check(code, DIFFCODE::SIDEFLAGS, result); }

	/// Worker function to set the area indicated by mask to specified result
	void Set(int mask, unsigned result) { diffcode &= (~mask); diffcode |= result; }
	/// Convenience function to set the side status, eg, SetSide(DIFFCODE::LEFT)
	void SetSide(unsigned result) { Set(DIFFCODE::SIDEFLAGS, result); }
public:

	// file/directory
	bool isDirectory() const { return Check(diffcode, DIFFCODE::TYPEFLAGS, DIFFCODE::DIR); }
	// left/right
	bool isSideFirstOnly() const { return CheckSide(diffcode, DIFFCODE::FIRST); }
	bool isSideSecondOnly() const { return CheckSide(diffcode, DIFFCODE::SECOND); }
	bool isSideThirdOnly() const { return CheckSide(diffcode, DIFFCODE::THIRD); }
	bool isMissingFirstOnly() const { return CheckSide(diffcode, DIFFCODE::SECOND | DIFFCODE::THIRD); }
	bool isMissingSecondOnly() const { return CheckSide(diffcode, DIFFCODE::FIRST | DIFFCODE::THIRD); }
	bool isMissingThirdOnly() const { return CheckSide(diffcode, DIFFCODE::FIRST | DIFFCODE::SECOND); }
	bool isSideBoth() const { return CheckSide(diffcode, DIFFCODE::BOTH); }
	bool isSideAll() const { return CheckSide(diffcode, DIFFCODE::ALL); }
	void setSideNone() { SetSide(0); }
	void setSideFlag(int nIndex)
	{
		switch (nIndex)
		{
		case 0: SetSide(diffcode | DIFFCODE::FIRST); return;
		case 1: SetSide(diffcode | DIFFCODE::SECOND); return;
		case 2: SetSide(diffcode | DIFFCODE::THIRD); return;
		}
	}
	void unsetSideFlag(int nIndex)
	{
		switch (nIndex)
		{
		case 0: SetSide(diffcode & ~DIFFCODE::FIRST); return;
		case 1: SetSide(diffcode & ~DIFFCODE::SECOND); return;
		case 2: SetSide(diffcode & ~DIFFCODE::THIRD); return;
		}
	}
	bool isSideOnly(int nIndex) const
	{
		switch (nIndex)
		{
		case 0: return isSideFirstOnly();
		case 1: return isSideSecondOnly();
		case 2: return isSideThirdOnly();
		default: return 0;
		}
	}
	bool existsFirst() const { return !!(diffcode & DIFFCODE::FIRST); }
	bool existsSecond() const { return !!(diffcode & DIFFCODE::SECOND); }
	bool existsThird() const { return !!(diffcode & DIFFCODE::THIRD); }
	bool exists(int nIndex) const
	{
		switch (nIndex)
		{
		case 0: return !!(diffcode & DIFFCODE::FIRST);
		case 1: return !!(diffcode & DIFFCODE::SECOND);
		case 2: return !!(diffcode & DIFFCODE::THIRD);
		default: return 0;
		}
	}
	bool existAll() const
	{
		return (isThreeway() ? DIFFCODE::ALL : DIFFCODE::BOTH) == (diffcode & DIFFCODE::ALL);
	}
	bool isThreeway() const
	{
		return (diffcode & DIFFCODE::THREEWAY) != 0;
	}

	// compare result
	bool isResultNone() const { return CheckCompare(diffcode, 0); }
	bool isResultSame() const { return CheckCompare(diffcode, DIFFCODE::SAME); }
	bool isResultDiff() const { return (CheckCompare(diffcode, DIFFCODE::DIFF) && !isResultFiltered() &&
			existAll()); }
	static bool isResultError(unsigned code) { return CheckCompare(code, DIFFCODE::CMPERR); }
	bool isResultError() const { return isResultError(diffcode); }
	static bool isResultAbort(unsigned code) { return CheckCompare(code, DIFFCODE::CMPABORT); }
	bool isResultAbort() const { return isResultAbort(diffcode); }
	// filter status
	bool isResultFiltered() const { return CheckFilter(diffcode, DIFFCODE::SKIPPED); }
	// type
	bool isExprDiff() const { return Check(diffcode, DIFFCODE::EXPRFLAGS, DIFFCODE::EXPRDIFF); }
	bool isText() const { return Check(diffcode, DIFFCODE::TEXTFLAGS, DIFFCODE::TEXT); }
	bool isBin() const { return (diffcode & DIFFCODE::BIN) != 0; }
	bool isImage() const { return (diffcode & DIFFCODE::IMAGE) != 0; }
	// rescan
	bool isScanNeeded() const { return ((diffcode & DIFFCODE::SCANFLAGS) == DIFFCODE::NEEDSCAN); }

	void swap(int idx1, int idx2);
};