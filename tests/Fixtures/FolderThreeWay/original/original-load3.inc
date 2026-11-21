void
CDiffWrapper::LoadWinMergeDiffsFromDiffUtilsScript3(
	struct change * script10, 
	struct change * script12,  
	struct change * script02,  
	const file_data * inf10, 
	const file_data * inf12,
	const file_data * inf02)
{
	DiffList diff10, diff12, diff02;
	diff10.Clear();
	diff12.Clear();
	diff02.Clear();

	const bool usefilters = m_options.m_filterCommentsLines ||
		m_options.m_bIgnoreMissingTrailingEol ||
		m_options.m_bIgnoreLineBreaks ||
		(m_pFilterList && m_pFilterList->HasRegExps()) ||
		(m_pSubstitutionList && m_pSubstitutionList->HasRegExps());
	
	for (int file = 0; file < 3; file++)
	{
		struct change *next = nullptr;
		int trans_a0, trans_b0, trans_a1, trans_b1;
		int first0, last0, first1, last1, deletes, inserts;
		OP_TYPE op;
		const file_data *pinf = nullptr;
		DiffList *pdiff = nullptr;
		PostFilterContext ctxt;

		switch (file)
		{
		case 0: next = script10; pdiff = &diff10; pinf = inf10; break;
		case 1: next = script12; pdiff = &diff12; pinf = inf12; break;
		case 2: next = script02; pdiff = &diff02; pinf = inf02; break;
		}

		while (next != nullptr)
		{
			/* Find a set of changes that belong together.  */
			struct change *thisob = next;
			struct change *end = find_change(next);
			
			/* Disconnect them from the rest of the changes,
			making them a hunk, and remember the rest for next iteration.  */
			next = end->link;
			end->link = nullptr;
#ifdef DEBUG
			debug_script(thisob);
#endif

			/* Print thisob hunk.  */
			//(*printfun) (thisob);
			{					
				/* Determine range of line numbers involved in each file.  */
				analyze_hunk (thisob, &first0, &last0, &first1, &last1, &deletes, &inserts, pinf);
			
				/* Reconnect the script so it will all be freed properly.  */
				end->link = next;

				if (deletes || inserts || thisob->trivial)
				{
					if (deletes && inserts)
						op = OP_DIFF;
					else if (deletes || inserts)
						op = OP_DIFF;
					else
						op = OP_TRIVIAL;
					
					/* Print the lines that the first file has.  */
					translate_range (&pinf[0], first0, last0, &trans_a0, &trans_b0);
					translate_range (&pinf[1], first1, last1, &trans_a1, &trans_b1);

					// Store information about these blocks in moved line info
					if (GetDetectMovedBlocks() && file != 2 /* diff02 */)
					{
						int index1 = 0;  // defaults for (file == 0 /* diff10 */)
						int index2 = 1;
						MovedLines::SIDE side1 = MovedLines::SIDE::RIGHT;
						MovedLines::SIDE side2 = MovedLines::SIDE::LEFT;
						if (file == 1 /* diff12 */)
						{
							index1 = 2;
							index2 = 1;
							side1 = MovedLines::SIDE::LEFT;
							side2 = MovedLines::SIDE::RIGHT;
						}
						if (index1 != -1 && index2 != -1)
						{
							if (thisob->match0>=0)
							{
								assert(thisob->inserted > 0);
								for (int i=0; i<thisob->inserted; ++i)
								{
									int line0 = i+thisob->match0 + (trans_a0-first0-1);
									int line1 = i+thisob->line1 + (trans_a1-first1-1);
									GetMovedLines(index1)->Add(side1, line1, line0);
								}
							}
							if (thisob->match1>=0)
							{
								assert(thisob->deleted > 0);
								for (int i=0; i<thisob->deleted; ++i)
								{
									int line0 = i+thisob->line0 + (trans_a0-first0-1);
									int line1 = i+thisob->match1 + (trans_a1-first1-1);
									GetMovedLines(index2)->Add(side2, line0, line1);
								}
							}
						}
					}

					int nTrivialInserts = 0;
					if (op != OP_TRIVIAL && usefilters)
						nTrivialInserts = PostFilter(ctxt, thisob, pinf);
					if (nTrivialInserts)
					{
						while (thisob != next)
						{
							op = (thisob->trivial) ? OP_TRIVIAL : OP_DIFF;
							first0 = thisob->line0;
							first1 = thisob->line1;
							last0 = first0 + thisob->deleted - 1;
							last1 = first1 + thisob->inserted - 1;
							translate_range (&pinf[0], first0, last0, &trans_a0, &trans_b0);
							translate_range (&pinf[1], first1, last1, &trans_a1, &trans_b1);

							AddDiffRange(pdiff, trans_a0-1, trans_b0-1, trans_a1-1, trans_b1-1, op);
							thisob = thisob->link;
						}
					}
					else
					{
						if (thisob->trivial)
							op = OP_TRIVIAL;
						AddDiffRange(pdiff, trans_a0-1, trans_b0-1, trans_a1-1, trans_b1-1, op);
					}
				}
			}
		}
	}

	Make3wayDiff(m_pDiffList->GetDiffRangeInfoVector(), diff10.GetDiffRangeInfoVector(), diff12.GetDiffRangeInfoVector(), 
		Comp02Functor(inf10, inf12), 
		(usefilters || m_options.m_bIgnoreBlankLines));

	if (GetDetectMovedBlocks())
		m_pDiffList->GetDiffRangeInfoVector() = InsertMovedBlocks3Way();
}
