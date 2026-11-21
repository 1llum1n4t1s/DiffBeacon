if (functionName == "substr" && args && args->size() >= 2 &&
		dynamic_cast<StringLiteral*>((*args)[0]) && dynamic_cast<IntLiteral*>((*args)[1]) &&
		(args->size() == 2 || dynamic_cast<IntLiteral*>((*args)[2])))
		return ReplaceFunctionWithLiteral(this, std::get<std::string>(func(ectxt, args)));