ExprNode* NegateNode::Optimize()
{
	if (!right)
		return this;
	right = right->Optimize();
	auto rInt = getConstIntValue(right);
	if (rInt)
	{
		delete this;
		return new IntLiteral(-*rInt);
	}
	auto rDouble = dynamic_cast<DoubleLiteral*>(right);
	if (rDouble)
	{
		const double doubleVal = -rDouble->value;
		delete this;
		return new DoubleLiteral(doubleVal);
	}
	return this;
}