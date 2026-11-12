if (functionName == "abs" && args)
	{
		if (dynamic_cast<IntLiteral*>((*args)[0]))
			return ReplaceFunctionWithLiteral(this, std::get<int64_t>(func(ectxt, args)));
		if (dynamic_cast<DoubleLiteral*>((*args)[0]))
			return ReplaceFunctionWithLiteral(this, std::get<double>(func(ectxt, args)));
	}