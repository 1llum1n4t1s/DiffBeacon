static bool IsArrayConst(const std::vector<ExprNode*>* args)
{
	if (!args) return false;
	for (auto& arg : *args)
	{
		if (!dynamic_cast<IntLiteral*>(arg) &&
			!dynamic_cast<DoubleLiteral*>(arg) &&
			!dynamic_cast<StringLiteral*>(arg) &&
			!dynamic_cast<DateTimeLiteral*>(arg) &&
			!dynamic_cast<BoolLiteral*>(arg))
		{
			return false;
		}
	}
	return true;
}