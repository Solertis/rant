﻿using System;
using System.Linq;

using Rant.Engine.Syntax;
using Rant.Vocabulary;
using Rant.Stringes;
using Rant.Engine.Syntax.Expressions;
using Rant.Engine.Syntax.Expressions.Operators;

using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Rant.Engine.Compiler
{
	internal class RantExpressionCompiler
	{
		internal enum ExpressionReadType
		{
			Expression,
			/// <summary>
			/// Read actions for groups (operations in parentheses)
			/// </summary>
			ExpressionGroup,
			/// <summary>
			/// Read actions for the value of a variable
			/// </summary>
			VariableValue,
			/// <summary>
			/// Read actions for a function body.
			/// </summary>
			FunctionBody,
            /// <summary>
            /// An expression group, except it won't allow REAArgumentSeperators
            /// (For if statements and things)
            /// </summary>
            ExpressionGroupNoList,
			/// <summary>
			/// Expression block
			/// </summary>
			ExpressionBlock,
            /// <summary>
            /// JSON-like object
            /// </summary>
			KeyValueObject,
            /// <summary>
            /// Read a value in an object
            /// </summary>
			KeyValueObjectValue,
            /// <summary>
            /// Read the value in a bracket value access
            /// </summary>
			BracketValue,
            /// <summary>
            /// A list is expected.
            /// </summary>
            List,
            /// <summary>
            /// A boolean value to invert is expected.
            /// </summary>
            InvertValue
		}

		private string _sourceName, _source;
		private TokenReader _reader;
		private List<string> _keywords;
        // a hack for VariableValue and FunctionBody going past end of expression
        private bool _hitEndOfExpr = false;

		public RantExpressionCompiler(string sourceName, string source, TokenReader reader)
		{
			_sourceName = sourceName;
			_source = source;
			_reader = reader;
			_keywords = new List<string>()
			{
				"var",
				"function",
				"true",
                "false",
				"no",
				"maybe",
				"list",
				"if",
				"return",
                "while",
                "break",
                "for"
			};
		}

		public RantAction Read(ExpressionReadType type = ExpressionReadType.Expression)
		{
			List<RantExpressionAction> actions = new List<RantExpressionAction>();

			Token<R> token = null;
            if (type == ExpressionReadType.Expression)
                _hitEndOfExpr = false;

			while (!_reader.End)
			{
                if (_hitEndOfExpr) goto done;
                token = _reader.ReadToken();

				switch (token.ID)
				{
					case R.EOF:
						throw new RantCompilerException(_sourceName, token, "Unexpected end of file.");
                    case R.Whitespace:
                        if (type == ExpressionReadType.InvertValue)
                            goto done;
                        break;
                    case R.LeftSquare:
						{
                            if ((!actions.Any() && type == ExpressionReadType.VariableValue) || type == ExpressionReadType.List)
                            {
                                actions.Add((Read(ExpressionReadType.List) as RAExpression).Group);
                                goto done;
                            }
							var val = Read(ExpressionReadType.BracketValue) as RAExpression;
							var obj = actions.Last();
							actions.RemoveAt(actions.Count - 1);
							actions.Add(new REAObjectProperty(token, val.Group, obj));
						}
						break;
					case R.RightSquare:
                        if (type == ExpressionReadType.List)
                            goto done;
                        if (
                            type == ExpressionReadType.FunctionBody ||
                            type == ExpressionReadType.VariableValue)
                            _hitEndOfExpr = true;
						if (
							type != ExpressionReadType.Expression && 
							type != ExpressionReadType.VariableValue && 
							type != ExpressionReadType.BracketValue &&
							type != ExpressionReadType.FunctionBody)
							SyntaxError(token, "Unexpected end of expression.");
						goto done;
					case R.LeftParen:
                        {
                            var group = (Read(ExpressionReadType.ExpressionGroup) as RAExpression).Group;
                            // could be a function call
                            if (actions.Any() &&
                                (actions.Last() is REAPatternString ||
                                 actions.Last() is REAVariable ||
                                 actions.Last() is REAFunctionCall ||
                                 actions.Last() is REAGroup ||
                                 actions.Last() is REAObjectProperty))
                            {
                                var last = actions.Last();
                                actions.Remove(last);
                                actions.Add(new REAFunctionCall(token, last, group as REAGroup));
                                break;
                            }
                            // could be a function definition
                            if (_reader.PeekLooseToken().ID == R.Equal)
                            {
                                _reader.ReadLooseToken();
                                _reader.ReadLoose(R.RightAngle, "fat arrow");
                                var body = Read(ExpressionReadType.FunctionBody);
                                actions.Add(new REAFunction(token, (body as RAExpression).Group, group as REAGroup));
                                if (type == ExpressionReadType.VariableValue)
                                    goto done;
                                break;
                            }
                            if (!group.Actions.Any())
                                Unexpected(token);
                            if (group.Actions.Any(x => x is REAArgumentSeperator))
                                actions.Add(CreateListFromActions(type, group.Actions));
                            else
                                actions.Add(group);
                        }
						break;
					case R.RightParen:
                        if (type == ExpressionReadType.ExpressionGroup || type == ExpressionReadType.VariableValue || type == ExpressionReadType.ExpressionGroupNoList)
                            goto done;
						break;
					case R.LeftCurly:
						// is it an object? let's find out
						if (_reader.PeekLooseToken().ID == R.RightCurly)
						{
							// empty object
							_reader.Read(R.RightCurly);
							actions.Add(new REAObject(token, new REAObjectKeyValue[0]));
							break;
						}
						// maybe it's an object with key/values
						var tempPosition = _reader.Position;
						if (_reader.PeekLooseToken().ID == R.ConstantLiteral)
						{
							var firstValue = _reader.ReadLooseToken();
							// ok, it's an object
							if (_reader.PeekLooseToken().ID == R.Colon)
							{
								_reader.Position = tempPosition;
								var obj = Read(ExpressionReadType.KeyValueObject);
								actions.Add(obj as RantExpressionAction);
								break;
							}
							else
								_reader.Position = tempPosition;
						}
						if (type == ExpressionReadType.FunctionBody || type == ExpressionReadType.Expression)
						{
							var body = Read(ExpressionReadType.ExpressionBlock);
							actions.Add(body as RantExpressionAction);
                            if (type == ExpressionReadType.FunctionBody)
                                goto done;
							break;
						}
						Unexpected(token);
						break;
					case R.RightCurly:
						if (type == ExpressionReadType.ExpressionBlock)
							return new REABlock(token, actions);
						if (type == ExpressionReadType.KeyValueObjectValue)
							return new REAGroup(actions, token, _sourceName);
						Unexpected(token);
						break;
					case R.Semicolon:
						if (type != ExpressionReadType.Expression && type != ExpressionReadType.ExpressionBlock)
							goto done;
						if (actions.Count == 0)
							break;
						var action = new REAGroup(actions.ToList(), token, _sourceName);
						actions.Clear();
						actions.Add(action);
						break;
					case R.ConstantLiteral:
						if (type == ExpressionReadType.KeyValueObject)
						{
							var name = token;
							_reader.ReadLoose(R.Colon, "key value seperator");
							var value = Read(ExpressionReadType.KeyValueObjectValue) as RantExpressionAction;
							actions.Add(new REAObjectKeyValue(name, value));
							if (_reader.PrevLooseToken.ID == R.RightCurly)
							{
								List<REAObjectKeyValue> keyValues = new List<REAObjectKeyValue>();
								foreach (RantExpressionAction kv in actions)
								{
									if (!(kv is REAObjectKeyValue))
										throw new RantCompilerException(_sourceName, token, "Unexpected token in key value object.");
									keyValues.Add(kv as REAObjectKeyValue);
								}
								return new REAObject(name, keyValues.ToArray());
							}
							break;
						}
						actions.Add(new REAString(Util.UnescapeConstantLiteral(token.Value), token));
						break;
					case R.Dollar:
						if (_reader.PeekType() == R.ConstantLiteral)
						{
							var value = _reader.Read(R.ConstantLiteral, "string");
							actions.Add(new REAPatternString(Util.UnescapeConstantLiteral(value.Value), token));
							break;
						}
						Unexpected(token);
						break;
					case R.Text:
						// numbers
						if (char.IsDigit(token.Value[0]))
						{
                            if (actions.Any() && actions.Last() is REANumber)
                                Unexpected(token);
							double number;
							if (!ReadNumber(token, out number))
								SyntaxError(token, "Invalid number: " + token.Value);
							actions.Add(new REANumber(number, token));
							break;
						}
						// keywords
						if (_keywords.IndexOf(token.Value) >= 0)
						{
							switch (token.Value)
							{
								case "var":
									var name = _reader.ReadLoose(R.Text, "variable name");
                                    if (_keywords.IndexOf(name.Value) > -1)
                                        SyntaxError(name, "Cannot use reserved variable name.");
									if (_reader.PeekType() == R.Equal)
									{
										_reader.ReadToken();
										var val = Read(ExpressionReadType.VariableValue) as RAExpression;
										actions.Add(new REAObjectPropertyAssignment(name, null, val.Group));
									}
									else if (_reader.PeekType() == R.Semicolon || _reader.End)
										actions.Add(new REAObjectPropertyAssignment(name, null, null));
									break;
								case "function":
									{
										_reader.ReadLoose(R.LeftParen, "function definition open paren");
										var argGroup = Read(ExpressionReadType.ExpressionGroup) as RAExpression;
										_reader.ReadLoose(R.LeftCurly, "function body open bracket");
										var body = Read(ExpressionReadType.ExpressionBlock);
										actions.Add(new REAFunction(token, body as RantExpressionAction, argGroup.Group));
										if (type == ExpressionReadType.VariableValue)
											goto done;
									}
									break;
								case "true":
									actions.Add(new REABoolean(token, true));
									break;
								case "false":
									actions.Add(new REABoolean(token, false));
									break;
                                case "no":
                                    actions.Add(new REANull(token));
                                    break;
								case "maybe":
									actions.Add(new REAMaybe(token));
									break;
								case "list":
									{
										var num = _reader.ReadLoose(R.Text, "list length");
										double listLength = -1;
										ReadNumber(num, out listLength);
										if (listLength < 0)
											SyntaxError(num, "Invalid list length.");
										List<RantExpressionAction> fakeList = new List<RantExpressionAction>();
										for (var i = 0; i < listLength; i++)
											fakeList.Add(null);
										actions.Add(new REAList(token, fakeList, false));
									}
									break;
								case "if":
									{
										if (type != ExpressionReadType.Expression && type != ExpressionReadType.ExpressionBlock && type != ExpressionReadType.FunctionBody)
											Unexpected(token);
										_reader.ReadLoose(R.LeftParen, "if statement param");
										var val = Read(ExpressionReadType.ExpressionGroupNoList) as RAExpression;
										var body = Read(ExpressionReadType.FunctionBody) as RAExpression;
                                        RantExpressionAction elseBody = null;
                                        if (!_reader.End && _reader.PeekLooseToken().Value == "else")
                                        {
                                            _reader.ReadLooseToken();
                                            elseBody = (Read(ExpressionReadType.FunctionBody) as RAExpression).Group;
                                        }
										actions.Add(new REAIfStatement(token, val.Group, body.Group, elseBody));
									}
									break;
								case "return":
									{
										if (type != ExpressionReadType.ExpressionBlock && type != ExpressionReadType.FunctionBody && type != ExpressionReadType.Expression)
											Unexpected(token);
                                        RantExpressionAction expr = null;
                                        if (_reader.PeekLooseToken().ID != R.Semicolon)
                                            expr = (Read(ExpressionReadType.VariableValue) as RAExpression).Group;
                                        actions.Add(new REAReturn(token, expr));
									}
									break;
                                case "while":
                                    {
                                        if (type != ExpressionReadType.Expression && type != ExpressionReadType.ExpressionBlock)
                                            Unexpected(token);
                                        _reader.Read(R.LeftParen, "while loop test");
                                        var test = (Read(ExpressionReadType.ExpressionGroupNoList) as RAExpression).Group;
                                        var body = (Read(ExpressionReadType.FunctionBody) as RAExpression).Group;
                                        actions.Add(new REAWhile(token, test, body));
                                    }
                                    break;
                                case "break":
                                    {
                                        if (type != ExpressionReadType.FunctionBody && type != ExpressionReadType.ExpressionBlock)
                                            Unexpected(token);
                                        actions.Add(new REABreak(token));
                                    }
                                    break;
                                case "for":
                                    {
                                        if (type != ExpressionReadType.Expression && type != ExpressionReadType.ExpressionBlock)
                                            Unexpected(token);
                                        _reader.ReadLoose(R.LeftParen, "for loop open paren");
                                        var indexName = _reader.ReadLoose(R.Text, "for loop index name");
                                        if (indexName.Value == "var")
                                            indexName = _reader.ReadLoose(R.Text, "for loop index name");
                                        if (indexName.Value == "in")
                                            SyntaxError(indexName, "Expected index name in for loop.");
                                        var inStmt = _reader.ReadLoose(R.Text, "for loop in statement");
                                        if (inStmt.Value != "in")
                                            SyntaxError(inStmt, "Expected in statement in for loop.");
                                        var expr = (Read(ExpressionReadType.ExpressionGroupNoList) as RAExpression).Group;
                                        var body = (Read(ExpressionReadType.FunctionBody) as RAExpression).Group;
                                        actions.Add(new REAFor(token, indexName.Value, body, expr));
                                    }
                                    break;
							}
							break;
						}
						// just a variable
						actions.Add(new REAVariable(token));
						break;
					// binary number operators
					case R.Plus:
                        // increment operator
                        if (!_reader.End && _reader.PeekType() == R.Plus)
                        {
                            _reader.ReadToken();
                            // postfix
                            if (actions.Any() && actions.Last() is REAVariable)
                            {
                                var variable = actions.Last();
                                actions.RemoveAt(actions.Count - 1);
                                actions.Add(new REAPostfixIncDec(token) { LeftSide = variable });
                                break;
                            }
                            if (!_reader.End && _reader.PeekLooseToken().ID == R.Text)
                            {
                                var varName = _reader.ReadLooseToken();
                                double dummyNumber = -1;
                                if (ReadNumber(varName, out dummyNumber))
                                    SyntaxError(token, "Cannot increment constant.");
                                actions.Add(new REAPrefixIncDec(token) { RightSide = new REAVariable(varName) });
                                break;
                            }
                            Unexpected(token);
                        }
						actions.Add(new REAAdditionOperator(token));
						break;
					case R.Hyphen:
                        // decrement operator
                        if (!_reader.End && _reader.PeekType() == R.Hyphen)
                        {
                            _reader.ReadToken();
                            // postfix
                            if (actions.Any() && actions.Last() is REAVariable)
                            {
                                var variable = actions.Last();
                                actions.RemoveAt(actions.Count - 1);
                                actions.Add(new REAPostfixIncDec(token) { LeftSide = variable, Increment = false });
                                break;
                            }
                            if (!_reader.End && _reader.PeekLooseToken().ID == R.Text)
                            {
                                var varName = _reader.ReadLooseToken();
                                actions.Add(new REAPrefixIncDec(token) { Increment = false, RightSide = new REAVariable(varName) });
                                break;
                            }
                            Unexpected(token);
                        }

                        // could be negative sign on number - is there a digit directly following this?
                        if (char.IsDigit(_reader.PeekToken().Value[0]) && _reader.PrevToken.ID != R.Whitespace)
						{
							double number;
							if (!ReadNumber(token, out number))
								SyntaxError(token, "Invalid number: " + token.Value);
							actions.Add(new REANumber(number, token));
							break;
						}
						actions.Add(new REASubtractionOperator(token));
						break;
					case R.Comma:
						if (type == ExpressionReadType.KeyValueObjectValue)
							return new REAGroup(actions, token, _sourceName);
						if (type == ExpressionReadType.ExpressionGroup || type == ExpressionReadType.VariableValue || type == ExpressionReadType.List || type == ExpressionReadType.Expression)
						{
							actions.Add(new REAArgumentSeperator(token));
							break;
						}
                        Unexpected(token);
                        break;
					case R.ForwardSlash:
						actions.Add(new READivisionOperator(token));
						break;
					case R.Asterisk:
						actions.Add(new REAMultiplicationOperator(token));
						break;
					case R.Tilde:
						actions.Add(new REAConcatOperator(token));
						break;
					// accessing properties
					case R.Subtype:
						{
							var name = _reader.Read(R.Text, "object property access");
							var obj = actions.Last();
							actions.Remove(obj);
						    actions.Add(new REAObjectProperty(name, obj));
						}
						break;
					case R.LeftAngle:
						if (_reader.PeekType() == R.Equal)
						{
							_reader.ReadToken();
							actions.Add(new REALessThanOperator(token, true));
						}
						else
							actions.Add(new REALessThanOperator(token, false));
						break;
					case R.RightAngle:
						if (_reader.PeekType() == R.Equal)
						{
							_reader.ReadToken();
							actions.Add(new REAGreaterThanOperator(token, true));
						}
						else
							actions.Add(new REAGreaterThanOperator(token, false));
						break;
                    case R.Exclamation:
                        if (!_reader.End && _reader.PeekToken().ID == R.Equal)
                        {
                            _reader.ReadToken();
                            actions.Add(new REAInequalityOperator(token));
                            break;
                        }
                        // invert operator maybe
                        if (!_reader.End && (_reader.PeekLooseToken().ID == R.LeftParen || _reader.PeekLooseToken().ID == R.Text))
                        {
                            RantExpressionAction rightSide = null;
                            if (_reader.PeekLooseToken().ID == R.LeftParen)
                            {
                                _reader.ReadLooseToken();
                                rightSide = (Read(ExpressionReadType.ExpressionGroup) as RAExpression).Group;
                                actions.Add(new REAPrefixInvert(token) { RightSide = rightSide });
                                break;
                            }
                            rightSide = (Read(ExpressionReadType.InvertValue) as RAExpression).Group;
                            actions.Add(new REAPrefixInvert(token) { RightSide = rightSide });
                            break;
                        }
                        Unexpected(token);
                        break;
					case R.Equal:
						if (_reader.PeekType() == R.Equal)
						{
							_reader.ReadToken();
							actions.Add(new REAEqualityOperator(token));
							break;
						}
                        REAInfixOperator op = null;
                        if (!actions.Any())
                            SyntaxError(token, "Cannot assign value to nothing.");
                        if (actions.Last() is REAInfixOperator)
                        {
                            if (_reader.PrevToken.ID == R.Whitespace)
                                Unexpected(token);
                            op = actions.Last() as REAInfixOperator;
                            if (op.Type == RantExpressionAction.ActionValueType.Boolean)
                                Unexpected(token);
                            actions.RemoveAt(actions.Count - 1);
                            if (!actions.Any())
                                Unexpected(token);
                        }
                        // variable assignment
                        if (actions.Last() is REAVariable)
                        {
                            var lastAction = actions.Last();
                            var val = (Read(ExpressionReadType.VariableValue) as RAExpression).Group;
                            actions.RemoveAt(actions.Count - 1);

                            if (_keywords.IndexOf(lastAction.Range.Value) > -1)
                                SyntaxError(lastAction.Range, "Cannot use reserved variable name.");
                            var assignment = new REAObjectPropertyAssignment(lastAction.Range, null, val);
                            assignment.Operator = op;
                            actions.Add(assignment);
                            if (type == ExpressionReadType.FunctionBody)
                                goto done;
                            break;
                        }
                        // object property assignment
                        if (actions.Last() is REAObjectProperty)
                        {
                            var lastAction = actions.Last() as REAObjectProperty;
                            var value = Read(ExpressionReadType.VariableValue) as RAExpression;
                            var assignment = new REAObjectPropertyAssignment(lastAction.Range, lastAction.Object, value.Group);
                            assignment.Operator = op;
                            actions.Add(assignment);
                            break;
                        }
                        Unexpected(token);
						break;
                    case R.Undefined:
                        if (!_reader.End && _reader.PeekLooseToken().ID != R.Semicolon)
                            Unexpected(_reader.PeekLooseToken());
                        actions.Add(new REAUndefined(token));
                        break;
                    case R.Ampersand:
                        // boolean AND
                        if (!_reader.End && _reader.PeekType() == R.Ampersand)
                        {
                            _reader.ReadToken();
                            actions.Add(new REABooleanAndOperator(token));
                            break;
                        }
                        Unexpected(token);
                        break;
                    case R.Pipe:
                        // boolean OR
                        if (!_reader.End && _reader.PeekType() == R.Pipe)
                        {
                            _reader.ReadToken();
                            actions.Add(new REABooleanOrOperator(token));
                            break;
                        }
                        Unexpected(token);
                        break;
					default:
						Unexpected(token);
						break;
				}
			}

            done:

            if (actions.Any(x => x is REAArgumentSeperator) &&
                (type == ExpressionReadType.Expression ||
                type == ExpressionReadType.VariableValue || type == ExpressionReadType.List))
            {
                // lists
                var list = CreateListFromActions(type, actions);
                actions.Clear();
                actions.Add(list);
            }
            else if(actions.Where(x => x is REAArgumentSeperator).Any() && type != ExpressionReadType.ExpressionGroup)
                Unexpected(actions.Where(x => x is REAArgumentSeperator).First().Range);

			switch (type)
			{
				case ExpressionReadType.ExpressionGroup:
                case ExpressionReadType.ExpressionGroupNoList:
                case ExpressionReadType.VariableValue:
				case ExpressionReadType.Expression:
				case ExpressionReadType.BracketValue:
                case ExpressionReadType.FunctionBody:
                case ExpressionReadType.List:
                case ExpressionReadType.InvertValue:
					return new RAExpression(actions, token, _sourceName);
				default:
					throw new RantCompilerException(_sourceName, token, "Unexpected end of expression.");
			}
		}

        private REAList CreateListFromActions(ExpressionReadType type, List<RantExpressionAction> actions)
        {
            List<RantExpressionAction> listActions = new List<RantExpressionAction>();
            RantExpressionAction lastItem = null;
            foreach (RantExpressionAction reAction in actions)
            {
                if (reAction is REAArgumentSeperator)
                {
                    listActions.Add(lastItem);
                    lastItem = null;
                }
                else
                    lastItem = reAction;
            }
            listActions.Add(lastItem);
            return new REAList(actions.First().Range, listActions, type != ExpressionReadType.List);
        }

		private bool ReadNumber(Token<R> token, out double number)
		{
			number = -1;
			string suffix = "";

			var initialString = token.Value;
			if (token.ID == R.Hyphen)
				initialString += _reader.Read(R.Text, "number").Value;
			if (_reader.PeekType() == R.Subtype)
			{
				initialString += _reader.ReadToken().Value;
				// read next text
				initialString += _reader.Read(R.Text, "number").Value;
			}
			if (char.IsLetter(initialString[initialString.Length - 1]))
			{
				suffix = initialString[initialString.Length - 1].ToString();
				if (suffix != "k" && suffix != "M" && suffix != "B")
					suffix = "";
				initialString = initialString.Substring(0, initialString.Length - 1);
            }
			if (!initialString.ToCharArray().All(x => char.IsDigit(x) || x == '.' || x == '-'))
				return false;
			return Util.ParseDouble(initialString + suffix, out number);
		}

		private void Unexpected(Stringe token)
		{
			throw new RantCompilerException(_sourceName, token, $"Unexpected token: '{token.Value}'");
		}

		private void SyntaxError(Stringe token, string message)
		{
			throw new RantCompilerException(_sourceName, token, message);
		}

		private void SyntaxError(Stringe token, Exception innerException)
		{
			throw new RantCompilerException(_sourceName, token, innerException);
		}
	}
}
