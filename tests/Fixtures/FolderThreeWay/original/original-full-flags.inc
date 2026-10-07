			m_ndiffs = diffList.GetSignificantDiffs(); 
			m_ntrivialdiffs = diffList.GetSize() - m_ndiffs;

			if (m_ndiffs > 0 || bin_flag10 < 0 || bin_flag12 < 0)
				code |= DIFFCODE::DIFF;
			else
				code |= DIFFCODE::SAME;
			if (bin_flag10 || bin_flag12)
				code |= DIFFCODE::BIN;
			else
				code |= DIFFCODE::TEXT;

			if ((code & DIFFCODE::COMPAREFLAGS) == DIFFCODE::DIFF)
			{
				if ((code & DIFFCODE::TEXTFLAGS) == DIFFCODE::TEXT)
				{
					if (CDiffWrapper::IsIdenticalOrIgnorable(script12))
						code |= DIFFCODE::DIFF1STONLY;
					else if (CDiffWrapper::IsIdenticalOrIgnorable(script02))
						code |= DIFFCODE::DIFF2NDONLY;
					else if (CDiffWrapper::IsIdenticalOrIgnorable(script10))
						code |= DIFFCODE::DIFF3RDONLY;
				}
				else
				{
					if (bin_flag12 > 0)
						code |= DIFFCODE::DIFF1STONLY;
					else if (bin_flag02 > 0)
						code |= DIFFCODE::DIFF2NDONLY;
					else if (bin_flag10 > 0)
						code |= DIFFCODE::DIFF3RDONLY;
				}
			}

			// If unique item, it was being compared to itself to determine encoding
			// and the #diffs is invalid
			if (di.diffcode.isSideFirstOnly() || di.diffcode.isSideSecondOnly() || di.diffcode.isSideThirdOnly())
			{
				m_ndiffs = CDiffContext::DIFFS_UNKNOWN;
				m_ntrivialdiffs = CDiffContext::DIFFS_UNKNOWN;
			}

