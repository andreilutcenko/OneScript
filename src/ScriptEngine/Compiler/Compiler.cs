﻿/*----------------------------------------------------------
This Source Code Form is subject to the terms of the 
Mozilla Public License, v.2.0. If a copy of the MPL 
was not distributed with this file, You can obtain one 
at http://mozilla.org/MPL/2.0/.
----------------------------------------------------------*/
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using ScriptEngine.Machine;

namespace ScriptEngine.Compiler
{
    [Flags]
    public enum CodeGenerationFlags
    {
        Always,
        CodeStatistics,
        DebugCode
    }

    class Compiler    {
        public const string BODY_METHOD_NAME = "$entry";

        private static readonly Dictionary<Token, OperationCode> _tokenToOpCode;

        private Parser _parser;
        private ICompilerContext _ctx;
        private ModuleImage _module;
        private Lexem _previousExtractedLexem;
        private Lexem _lastExtractedLexem;
        private bool _inMethodScope = false;
        private bool _isMethodsDefined = false;
        private bool _isStatementsDefined = false;
        private bool _isFunctionProcessed = false;
        private bool _isInTryBlock = false;

        private readonly Stack<Token[]> _tokenStack = new Stack<Token[]>();
        private readonly Stack<NestedLoopInfo> _nestedLoops = new Stack<NestedLoopInfo>();
        private readonly List<ForwardedMethodDecl> _forwardedMethods = new List<ForwardedMethodDecl>();
        private readonly List<AnnotationDefinition> _annotations = new List<AnnotationDefinition>();

        private struct ForwardedMethodDecl
        {
            public string identifier;
            public bool[] factArguments;
            public bool asFunction;
            public int codeLine;
            public int commandIndex;
        }

        private struct NestedLoopInfo
        {
            public static NestedLoopInfo New()
            {
                return new NestedLoopInfo()
                {
                    startPoint = -1,
                    breakStatements = new List<int>()
                };
            }

            public int startPoint;
            public List<int> breakStatements;
        }

        public CompilerDirectiveHandler DirectiveHandler { get; set; }
    
        public CodeGenerationFlags ProduceExtraCode { get; set; }

        public ModuleImage Compile(Parser parser, ICompilerContext context)
        {
            _module = new ModuleImage();
            _module.LoadAddress = context.TopIndex();
            _ctx = context;
            _parser = parser;
            _parser.Start();

            BuildModule();
            CheckForwardedDeclarations();

            return _module;
        }

        public ModuleImage CompileExpression(Parser parser, ICompilerContext context)
        {
            _module = new ModuleImage();
            _module.LoadAddress = context.TopIndex();
            _ctx = context;
            _parser = parser;
            _parser.Start();
            NextToken();
            BuildExpression(Token.EndOfText);

            return _module;
        }

        public ModuleImage CompileExecBatch(Parser parser, ICompilerContext context)
        {
            _module = new ModuleImage();
            _module.LoadAddress = context.TopIndex();
            _ctx = context;
            _parser = parser;
            _parser.Start();
            NextToken();
            PushStructureToken(Token.EndOfText);
            BuildModuleBody();

            return _module;
        }

        private AnnotationParameter BuildAnnotationParameter()
        {
            // id | id = value | value
            var result = new AnnotationParameter();
            if (_lastExtractedLexem.Type == LexemType.Identifier)
            {
                result.Name = _lastExtractedLexem.Content;
                NextToken();
                if (_lastExtractedLexem.Token != Token.Equal)
                {
                    result.ValueIndex = AnnotationParameter.UNDEFINED_VALUE_INDEX;
                    return result;
                }
                NextToken();
            }
            
            var cDef = CreateConstDefinition(ref _lastExtractedLexem);
            result.ValueIndex = GetConstNumber(ref cDef);
            
            NextToken();
            
            return result;
        }

        private IList<AnnotationParameter> BuildAnnotationParameters()
        {
            var parameters = new List<AnnotationParameter>();
            while (_lastExtractedLexem.Token != Token.EndOfText)
            {
                parameters.Add(BuildAnnotationParameter());
                if (_lastExtractedLexem.Token == Token.Comma)
                {
                    NextToken();
                    continue;
                }
                if (_lastExtractedLexem.Token == Token.ClosePar)
                {
                    NextToken();
                    break;
                }
                throw CompilerException.UnexpectedOperation();
            }
            return parameters;
        }

        private void BuildAnnotations()
        {
            while (_lastExtractedLexem.Type == LexemType.Annotation)
            {
                var annotation = new AnnotationDefinition() {Name = _lastExtractedLexem.Content};

                NextToken();
                if (_lastExtractedLexem.Token == Token.OpenPar)
                {
                    NextToken();
                    annotation.Parameters = BuildAnnotationParameters().ToArray();
                }
                
                _annotations.Add(annotation);
            }
        }

        private AnnotationDefinition[] ExtractAnnotations()
        {
            var result = _annotations.ToArray();
            _annotations.Clear();
            return result;
        }

        private void BuildModule()
        {
            NextToken();

            if (_lastExtractedLexem.Type == LexemType.EndOfText)
            {
                return;
            }

            try
            {
                DispatchModuleBuild();
            }
            catch (CompilerException exc)
            {
                if(exc.LineNumber == 0)
                    AppendCodeInfo(_parser.CurrentLine, exc);
                throw;
            }


        }

        private void CheckForwardedDeclarations()
        {
            if (_forwardedMethods.Count > 0)
            {
                foreach (var item in _forwardedMethods)
                {
                    SymbolBinding methN;
                    try
                    {
                        methN = _ctx.GetMethod(item.identifier);
                    }
                    catch (CompilerException exc)
                    {
                        AppendCodeInfo(item.codeLine, exc);
                        throw;
                    }

                    var scope = _ctx.GetScope(methN.ContextIndex);

                    var methInfo = scope.GetMethod(methN.CodeIndex);
                    System.Diagnostics.Debug.Assert(StringComparer.OrdinalIgnoreCase.Compare(methInfo.Name, item.identifier) == 0);
                    if (item.asFunction && !methInfo.IsFunction)
                    {
                        var exc = CompilerException.UseProcAsFunction();
                        AppendCodeInfo(item.codeLine, exc);
                        throw exc;
                    }

                    try
                    {
                        CheckFactArguments(methInfo, item.factArguments);
                    }
                    catch (CompilerException exc)
                    {
                        AppendCodeInfo(item.codeLine, exc);
                        throw;
                    }

                    var cmd = _module.Code[item.commandIndex];
                    cmd.Argument = GetMethodRefNumber(ref methN);
                    _module.Code[item.commandIndex] = cmd;
                }
            }
        }

        private void AppendCodeInfo(int line, CompilerException exc)
        {
            var cp = new CodePositionInfo();
            cp.LineNumber = line;
            cp.Code = _parser.GetCodeLine(line);
            CompilerException.AppendCodeInfo(exc, cp);
        }

        private void DispatchModuleBuild()
        {
            bool isCodeEntered = false;

            while (_lastExtractedLexem.Type != LexemType.EndOfText)
            {
                if (_lastExtractedLexem.Type == LexemType.Identifier)
                {
                    if (_lastExtractedLexem.Token == Token.VarDef)
                    {
                        isCodeEntered = true;
                        BuildVariableDefinitions();
                    }
                    else if (_lastExtractedLexem.Token == Token.Procedure || _lastExtractedLexem.Token == Token.Function)
                    {
                        isCodeEntered = true;
                        _isMethodsDefined = true;
                        BuildSingleMethod();
                    }
                    else
                    {
                        isCodeEntered = true;
                        BuildModuleBody();
                    }
                }
                else if(_lastExtractedLexem.Type == LexemType.Directive)
                {
                    HandleDirective(isCodeEntered);
                    UpdateCompositeContext(); // костыль для #330
                }
                else if (_lastExtractedLexem.Type == LexemType.Annotation)
                {
                    BuildAnnotations();
                }
                else
                {
                    throw CompilerException.UnexpectedOperation();
                }
            }
        }

        private void UpdateCompositeContext()
        {
            var modCtx = _ctx as ModuleCompilerContext;
            if (modCtx != null)
                modCtx.Update();
        }

        private void BuildVariableDefinitions()
        {
            while (_lastExtractedLexem.Token == Token.VarDef)
            {
                NextToken();
                while (true)
                {

                    if (IsUserSymbol(ref _lastExtractedLexem))
                    {
                        var symbolicName = _lastExtractedLexem.Content;
                        var annotations = ExtractAnnotations();
                        var definition = _ctx.DefineVariable(symbolicName);
                        if (_inMethodScope)
                        {
                            if (_isStatementsDefined)
                            {
                                throw CompilerException.LateVarDefinition();
                            }
                        }
                        else
                        {
                            if (_isMethodsDefined)
                            {
                                throw CompilerException.LateVarDefinition();
                            }

                            _module.VariableRefs.Add(definition);
                            _module.Variables.Add(new VariableInfo()
                            {
                                Identifier = symbolicName,
                                Annotations = annotations,
                                CanGet = true,
                                CanSet = true,
                                Index = definition.CodeIndex
                            });
                        }
                        NextToken();
                        if (_lastExtractedLexem.Token == Token.Export)
                        {
                            _module.ExportedProperties.Add(new ExportedSymbol()
                            {
                                SymbolicName = symbolicName,
                                Index = definition.CodeIndex
                            });
                            NextToken();
                        }
                        if (_lastExtractedLexem.Token == Token.Comma)
                        {
                            NextToken();
                            continue;
                        }
                        if (_lastExtractedLexem.Token != Token.Semicolon)
                        {
                            throw CompilerException.SemicolonExpected();
                        }
                        NextToken();
                        break;
                    }
                    else
                    {
                        throw CompilerException.IdentifierExpected();
                    }

                }
            }

        }

        private void BuildModuleBody()
        {
            _isFunctionProcessed = false;
            PushStructureToken(Token.EndOfText);
            var entry = _module.Code.Count;

            _ctx.PushScope(new SymbolScope());
            try
            {
                BuildCodeBatch();
            }
            catch
            {
                _ctx.PopScope();
                throw;
            }
            PopStructureToken();

            var localCtx = _ctx.PopScope();
            
            var topIdx = _ctx.TopIndex();

            if (entry != _module.Code.Count)
            {
                var bodyMethod = new MethodInfo();
                bodyMethod.Name = BODY_METHOD_NAME;
                var descriptor = new MethodDescriptor();
                descriptor.EntryPoint = entry;
                descriptor.Signature = bodyMethod;
                FillVariablesFrame(ref descriptor, localCtx);

                var entryRefNumber = _module.MethodRefs.Count;
                var bodyBinding = new SymbolBinding()
                {
                    ContextIndex = topIdx,
                    CodeIndex = _module.Methods.Count
                };
                _module.Methods.Add(descriptor);
                _module.MethodRefs.Add(bodyBinding);
                _module.EntryMethodIndex = entryRefNumber;
            }
        }

        private static void FillVariablesFrame(ref MethodDescriptor descriptor, SymbolScope localCtx)
        {
            descriptor.Variables = new VariablesFrame();

            for (int i = 0; i < localCtx.VariableCount; i++)
            {
                descriptor.Variables.Add(localCtx.GetVariable(i));
            }
            
        }

        private void HandleDirective(bool codeEntered)
        {
            var directive = _lastExtractedLexem.Content;
            var value = _parser.ReadLineToEnd().Trim();
            NextToken();

            if (DirectiveHandler == null || !DirectiveHandler(directive, value, codeEntered))
                throw new CompilerException(String.Format("Неизвестная директива: {0}({1})", directive, value));

        }

        private void BuildSingleMethod()
        {
            var entryPoint = _module.Code.Count;
            AddCommand(OperationCode.LineNum, _lastExtractedLexem.LineNumber, CodeGenerationFlags.CodeStatistics);

            if (_lastExtractedLexem.Token == Token.Procedure)
            {
                PushStructureToken(Token.EndProcedure);
                _isFunctionProcessed = false;
                NextToken();
            }
            else if (_lastExtractedLexem.Token == Token.Function)
            {
                PushStructureToken(Token.EndFunction);
                _isFunctionProcessed = true;
                NextToken();
            }
            else
            {
                throw CompilerException.UnexpectedOperation();
            }

            #region Method signature
            // сигнатура
            if (!IsUserSymbol(ref _lastExtractedLexem))
            {
                throw CompilerException.IdentifierExpected();
            }

            // issue #375
            if (String.Compare(_lastExtractedLexem.Content, "выполнить", StringComparison.OrdinalIgnoreCase) == 0
                || String.Compare(_lastExtractedLexem.Content, "execute", StringComparison.OrdinalIgnoreCase) == 0)
            {
                SystemLogger.Write($"WARNING! Method name '{_lastExtractedLexem.Content}' is DEPRECATED. Rename it");
            }

            int definitionLine = _parser.CurrentLine;
            MethodInfo method = new MethodInfo();
            method.Name = _lastExtractedLexem.Content;
            method.IsFunction = _isFunctionProcessed;
            method.Annotations = ExtractAnnotations();

            NextToken();
            if (_lastExtractedLexem.Token != Token.OpenPar)
            {
                throw CompilerException.TokenExpected(Token.OpenPar);
            } 
            #endregion
            
            #region Parameters list
            var paramsList = new List<ParameterDefinition>();
            var methodCtx = new SymbolScope();

            BuildMethodParametersList(paramsList, methodCtx);

            method.Params = paramsList.ToArray();
 
            #endregion
            
            bool isExportedMethod = false;
            if (_lastExtractedLexem.Token == Token.Export)
            {
                isExportedMethod = true;
                NextToken();
            }

            method.IsExport = isExportedMethod;

            #region Body
            // тело

            try
            {
                _ctx.PushScope(methodCtx);
                DispatchMethodBody();
            }
            finally
            {
                _ctx.PopScope();
            }
            
            PopStructureToken();

            var descriptor = new MethodDescriptor();
            descriptor.EntryPoint = entryPoint;
            descriptor.Signature = method;
            FillVariablesFrame(ref descriptor, methodCtx);

            SymbolBinding binding;
            try
            {
                binding = _ctx.DefineMethod(method);
            }
            catch (CompilerException)
            {
                var exc = new CompilerException("Метод с таким именем уже определен: " + method.Name);
                exc.LineNumber = definitionLine;
                exc.Code = _parser.GetCodeLine(exc.LineNumber);
                throw exc;
            }
            _module.MethodRefs.Add(binding);
            _module.Methods.Add(descriptor);

            // TODO: deprecate?
            if (isExportedMethod)
            {
                _module.ExportedMethods.Add(new ExportedSymbol()
                {
                    SymbolicName = method.Name,
                    Index = binding.CodeIndex
                });
            }

            #endregion

            NextToken(); 
            
        }

        private void BuildMethodParametersList(List<ParameterDefinition> paramsList, SymbolScope methodCtx)
        {
            NextToken(); // (
            while (_lastExtractedLexem.Token != Token.ClosePar)
            {
                // [Знач] Идентификатор[= Литерал][,[Знач] Идентификатор[= Литерал]]
                var param = BuildParameterDefinition();

                if (_lastExtractedLexem.Token == Token.Comma)
                {
                    paramsList.Add(param);
                    methodCtx.DefineVariable(param.Name);
                    NextToken();

                    if (_lastExtractedLexem.Token == Token.ClosePar) // сразу после запятой не можем выйти из цикла, т.к. ждем в этом месте параметр
                    {
                        throw CompilerException.IdentifierExpected();
                    }

                    continue;
                }
                
                paramsList.Add(param);
                methodCtx.DefineVariable(param.Name);
            }
            
            NextToken(); // )
        }

        private ParameterDefinition BuildParameterDefinition()
        {
            var param = new ParameterDefinition();
            
            if (_lastExtractedLexem.Type == LexemType.Annotation)
            {
                BuildAnnotations();
                param.Annotations = ExtractAnnotations();
            }
            
            if (_lastExtractedLexem.Token == Token.ByValParam)
            {
                param.IsByValue = true;
                NextToken();
                if (IsUserSymbol(ref _lastExtractedLexem))
                {
                    param.Name = _lastExtractedLexem.Content;
                }
                else
                {
                    throw CompilerException.IdentifierExpected();
                }
            }
            else if (IsUserSymbol(ref _lastExtractedLexem))
            {
                param.IsByValue = false;
                param.Name = _lastExtractedLexem.Content;
            }
            else
            {
                throw CompilerException.IdentifierExpected();
            }

            NextToken();
            if (_lastExtractedLexem.Token == Token.Equal)
            {
                param.HasDefaultValue = true;
                param.DefaultValueIndex = BuildDefaultParameterValue();
            }

            return param;
        }

        private int BuildDefaultParameterValue()
        {
            NextToken();

            bool hasSign = false;
            bool signIsMinus = _lastExtractedLexem.Token == Token.Minus;
            if (signIsMinus || _lastExtractedLexem.Token == Token.Plus)
            {
                hasSign = true;
                NextToken();
            }

            if (IsLiteral(ref _lastExtractedLexem))
            {
                var cd = CreateConstDefinition(ref _lastExtractedLexem);
                if (hasSign)
                {
                    if (_lastExtractedLexem.Type == LexemType.NumberLiteral && signIsMinus)
                    {
                        cd.Presentation = '-' + cd.Presentation;
                    }
                    else if (_lastExtractedLexem.Type == LexemType.StringLiteral
                          || _lastExtractedLexem.Type == LexemType.DateLiteral)
                    {
                        throw CompilerException.NumberExpected();
                    }
                }

                NextToken();
                return GetConstNumber(ref cd);
            }
            else
            {
                throw CompilerException.LiteralExpected();
            }
        }

        private void DispatchMethodBody()
        {
            _inMethodScope = true;
            BuildVariableDefinitions();
            _isStatementsDefined = true;

            var codeStart = _module.Code.Count;

            BuildCodeBatch();

            if (_isFunctionProcessed)
            {
                var undefConst = new ConstDefinition()
                {
                    Type = DataType.Undefined,
                    Presentation = "Неопределено"
                };

                AddCommand(OperationCode.PushConst, GetConstNumber(ref undefConst));
                
            }

            var codeEnd = _module.Code.Count;

            if (_lastExtractedLexem.Token == Token.EndProcedure
                || _lastExtractedLexem.Token == Token.EndFunction)
            {
                AddCommand(OperationCode.LineNum, _lastExtractedLexem.LineNumber, CodeGenerationFlags.CodeStatistics|CodeGenerationFlags.DebugCode);
            }

            AddCommand(OperationCode.Return, 0);

            {
                // заменим Return на Jmp <сюда>
                for (var i = codeStart; i < codeEnd; i++)
                {
                    if (_module.Code[i].Code == OperationCode.Return)
                    {
                        _module.Code[i] = new Command() { Code = OperationCode.Jmp, Argument = codeEnd };
                    }
                }
            }

            _isStatementsDefined = false;
            _inMethodScope = false;
        }

        private void BuildCodeBatch()
        {
            var endTokens = _tokenStack.Peek();

            while (true)
            {
                if (endTokens.Contains(_lastExtractedLexem.Token))
                {
                    return;
                }
                if (_lastExtractedLexem.Token == Token.Semicolon)
                {
                    NextToken();
                    continue;
                }

                if (_lastExtractedLexem.Type != LexemType.Identifier && _lastExtractedLexem.Token != Token.EndOfText)
                {
                    throw CompilerException.UnexpectedOperation();
                }

                if (_lastExtractedLexem.Token == Token.NotAToken)
                {
                    BuildSimpleStatement();
                }
                else
                {
                    BuildComplexStructureStatement();
                }

                if (_lastExtractedLexem.Token != Token.Semicolon)
                {
                    if (endTokens.Contains(_lastExtractedLexem.Token) || LanguageDef.IsEndOfBlockToken(_lastExtractedLexem.Token))
                    {
                        break;
                    }
                    throw CompilerException.SemicolonExpected();
                }
                NextToken();
            }

        }

        private void BuildComplexStructureStatement()
        {
            switch (_lastExtractedLexem.Token)
            {
                case Token.If:
                    BuildIfStatement();
                    break;
                case Token.For:
                    BuildForStatement();
                    break;
                case Token.While:
                    BuildWhileStatement();
                    break;
                case Token.Break:
                    BuildBreakStatement();
                    break;
                case Token.Continue:
                    BuildContinueStatement();
                    break;
                case Token.Return:
                    BuildReturnStatement();
                    break;
                case Token.Try:
                    BuildTryExceptStatement();
                    break;
                case Token.RaiseException:
                    BuildRaiseExceptionStatement();
                    break;
                case Token.Execute:
                    BuildExecuteStatement();
                    break;
                default:
                    var expected = PopStructureToken();
                    throw CompilerException.TokenExpected(expected);
            }
        }

        private void BuildIfStatement()
        {
            AddCommand(OperationCode.LineNum, _parser.CurrentLine);

            var exitIndices = new List<int>();
            NextToken();
            BuildExpression(Token.Then);
            PushStructureToken(Token.Else, Token.ElseIf, Token.EndIf);

            var jumpFalseIndex = AddCommand(OperationCode.JmpFalse, 0);

            NextToken();
            BuildCodeBatch();
            PopStructureToken();
            exitIndices.Add(AddCommand(OperationCode.Jmp, 0));

            bool hasAlternativeBranches = false;

            while (_lastExtractedLexem.Token == Token.ElseIf)
            {
                if (_lastExtractedLexem.Content.Equals("ElseIf", StringComparison.OrdinalIgnoreCase))
                {
                    SystemLogger.Write("WARNING! 'ElseIf' is deprecated! Use 'ElsIf' instead");
                }
                _module.Code[jumpFalseIndex] = new Command()
                {
                    Code = OperationCode.JmpFalse,
                    Argument = _module.Code.Count
                };
                AddCommand(OperationCode.LineNum, _lastExtractedLexem.LineNumber);

                NextToken();
                BuildExpression(Token.Then);
                PushStructureToken(Token.Else, Token.ElseIf, Token.EndIf);

                jumpFalseIndex = AddCommand(OperationCode.JmpFalse, -1);
                NextToken();
                BuildCodeBatch();
                PopStructureToken();
                exitIndices.Add(AddCommand(OperationCode.Jmp, -1));
            }

            if (_lastExtractedLexem.Token == Token.Else)
            {
                hasAlternativeBranches = true;
                _module.Code[jumpFalseIndex] = new Command()
                {
                    Code = OperationCode.JmpFalse,
                    Argument = _module.Code.Count
                };
                AddCommand(OperationCode.LineNum, _lastExtractedLexem.LineNumber, CodeGenerationFlags.CodeStatistics);

                NextToken();
                PushStructureToken(Token.EndIf);
                BuildCodeBatch();
                PopStructureToken();
            }

            int exitIndex;
            if (_lastExtractedLexem.Token == Token.EndIf)
            {
                exitIndex = AddCommand(OperationCode.LineNum, _lastExtractedLexem.LineNumber);
            }
            else
            {
                // Вообще, такого быть не должно...
                exitIndex = AddCommand(OperationCode.Nop, 0);
            }

            if (!hasAlternativeBranches)
            {
                _module.Code[jumpFalseIndex] = new Command()
                {
                    Code = OperationCode.JmpFalse,
                    Argument = exitIndex
                };
            }

            foreach (var indexToWrite in exitIndices)
            {
                _module.Code[indexToWrite] = new Command()
                {
                    Code = OperationCode.Jmp,
                    Argument = exitIndex
                };
            }
            NextToken();

        }

        private void BuildForStatement()
        {
            AddCommand(OperationCode.LineNum, _parser.CurrentLine);

            NextToken();
            if (_lastExtractedLexem.Token == Token.Each)
            {
                BuildForEachStatement();
            }
            else if (_lastExtractedLexem.Type == LexemType.Identifier)
            {
                BuildCountableForStatement();
            }
            else
            {
                throw CompilerException.IdentifierExpected();
            }


        }

        private void BuildForEachStatement()
        {
            Assert(_lastExtractedLexem.Token == Token.Each);
            NextToken();

            if (!IsUserSymbol(ref _lastExtractedLexem))
                throw CompilerException.IdentifierExpected();

            var identifier = _lastExtractedLexem.Content;
            NextToken();
            if(_lastExtractedLexem.Token != Token.In)
                throw CompilerException.TokenExpected(Token.In);

            NextToken();
            BuildExpression(Token.Loop);
            AddCommand(OperationCode.PushIterator, 0);
            var loopBegin = AddCommand(OperationCode.LineNum, _lastExtractedLexem.LineNumber);
            AddCommand(OperationCode.IteratorNext, 0);
            var condition = AddCommand(OperationCode.JmpFalse, -1);
            BuildLoadVariable(identifier);
            PushStructureToken(Token.EndLoop);

            var loopRecord = NestedLoopInfo.New();
            loopRecord.startPoint = loopBegin;
            _nestedLoops.Push(loopRecord);

            NextToken();
            bool savedTryFlag = SetTryBlockFlag(false);
            BuildCodeBatch();
            SetTryBlockFlag(savedTryFlag);
            PopStructureToken();

            if (_lastExtractedLexem.Token == Token.EndLoop)
            {
                AddCommand(OperationCode.LineNum, _lastExtractedLexem.LineNumber, CodeGenerationFlags.CodeStatistics | CodeGenerationFlags.DebugCode);
            }

            AddCommand(OperationCode.Jmp, loopBegin);
            var cmd = _module.Code[condition];
            cmd.Argument = AddCommand(OperationCode.StopIterator, 0);
            _module.Code[condition] = cmd;

            CorrectBreakStatements(_nestedLoops.Pop(), cmd.Argument);
            NextToken();
        }

        private void BuildCountableForStatement()
        {
            string counter = _lastExtractedLexem.Content;
            NextToken();
            if (_lastExtractedLexem.Token != Token.Equal)
            {
                throw CompilerException.TokenExpected(Token.Equal);
            }
            NextToken();
            BuildExpression(Token.To);
            BuildLoadVariable(counter);
            NextToken();
            BuildExpression(Token.Loop);
            AddCommand(OperationCode.MakeRawValue, 0);
            AddCommand(OperationCode.PushTmp, 0);
            var lastIdx = _module.Code.Count;
            int indexLoopBegin = -1;

            // TODO: костыль
            if (_lastExtractedLexem.Token == Token.Loop)
            {
                AddCommand(OperationCode.Jmp, lastIdx + 5);
                indexLoopBegin = AddCommand(OperationCode.LineNum, _lastExtractedLexem.LineNumber);
            }
            else
            {
                AddCommand(OperationCode.Jmp, lastIdx + 4);
            }

            {
                // increment
                var indexLoopBeginNew = BuildPushVariable(counter);
                if (indexLoopBegin == -1)
                    indexLoopBegin = indexLoopBeginNew;
            }

            AddCommand(OperationCode.Inc, 0);
            BuildLoadVariable(counter);

            BuildPushVariable(counter);
            var conditionIndex = AddCommand(OperationCode.JmpCounter, -1);
            var loopRecord = NestedLoopInfo.New();
            loopRecord.startPoint = indexLoopBegin;
            _nestedLoops.Push(loopRecord);
            NextToken();
            PushStructureToken(Token.EndLoop);
            bool savedTryFlag = SetTryBlockFlag(false);
            BuildCodeBatch();
            SetTryBlockFlag(savedTryFlag);
            PopStructureToken();

            if (_lastExtractedLexem.Token == Token.EndLoop)
            {
                AddCommand(OperationCode.LineNum, _lastExtractedLexem.LineNumber, CodeGenerationFlags.CodeStatistics | CodeGenerationFlags.DebugCode);
            }

            // jmp to start
            AddCommand(OperationCode.Jmp, indexLoopBegin);
            var indexLoopEnd = AddCommand(OperationCode.PopTmp, 1);

            var cmd = _module.Code[conditionIndex];
            cmd.Argument = indexLoopEnd;
            _module.Code[conditionIndex] = cmd;

            CorrectBreakStatements(_nestedLoops.Pop(), indexLoopEnd);
            NextToken();
            
        }

        private void BuildWhileStatement()
        {
            AddCommand(OperationCode.LineNum, _parser.CurrentLine);

            NextToken();
            var conditionIndex = _module.Code.Count;
            var loopRecord = NestedLoopInfo.New();
            loopRecord.startPoint = conditionIndex;
            _nestedLoops.Push(loopRecord);
            BuildExpression(Token.Loop);
            PushStructureToken(Token.EndLoop);

            var jumpFalseIndex = AddCommand(OperationCode.JmpFalse, 0);
            NextToken();
            bool savedTryFlag = SetTryBlockFlag(false);
            BuildCodeBatch();
            SetTryBlockFlag(savedTryFlag);
            PopStructureToken();

            if (_lastExtractedLexem.Token == Token.EndLoop)
            {
                AddCommand(OperationCode.LineNum, _lastExtractedLexem.LineNumber, CodeGenerationFlags.CodeStatistics | CodeGenerationFlags.DebugCode);
            }

            AddCommand(OperationCode.Jmp, conditionIndex);

            var endLoop = AddCommand(OperationCode.Nop, 0);
            _module.Code[jumpFalseIndex] = new Command() {
                Code = OperationCode.JmpFalse,
                Argument = endLoop
            };

            CorrectBreakStatements(_nestedLoops.Pop(), endLoop);

            NextToken();

        }

        private void BuildBreakStatement()
        {
            if (_nestedLoops.Count == 0)
            {
                throw CompilerException.BreakOutsideOfLoop();
            }
            AddCommand(OperationCode.LineNum, _parser.CurrentLine);

            var loopInfo = _nestedLoops.Peek();
            if(_isInTryBlock)
                AddCommand(OperationCode.EndTry, 0);
            var idx = AddCommand(OperationCode.Jmp, -1);
            loopInfo.breakStatements.Add(idx);
            NextToken();
        }

        private void BuildContinueStatement()
        {
            if (_nestedLoops.Count == 0)
            {
                throw CompilerException.ContinueOutsideOfLoop();
            }

            AddCommand(OperationCode.LineNum, _parser.CurrentLine);

            var loopInfo = _nestedLoops.Peek();
            if(_isInTryBlock)
                AddCommand(OperationCode.EndTry, 0);
            AddCommand(OperationCode.Jmp, loopInfo.startPoint);
            NextToken();
        }

        private void BuildReturnStatement()
        {
            AddCommand(OperationCode.LineNum, _parser.CurrentLine);

            if (_isFunctionProcessed)
            {
                NextToken();
                if (_lastExtractedLexem.Token == Token.Semicolon
                    || LanguageDef.IsEndOfBlockToken(_lastExtractedLexem.Token))
                {
                    throw CompilerException.FuncEmptyReturnValue();
                }
                BuildExpression(Token.Semicolon);
                AddCommand(OperationCode.MakeRawValue, 0);
            }
            else if (_inMethodScope)
            {
                NextToken();
                if (_lastExtractedLexem.Token != Token.Semicolon
                    && !LanguageDef.IsEndOfBlockToken(_lastExtractedLexem.Token))
                {
                    throw CompilerException.ProcReturnsAValue();
                }
            }
            else
            {
                throw CompilerException.ReturnOutsideOfMethod();
            }

            AddCommand(OperationCode.Return, 0);
        }

        private void BuildTryExceptStatement()
        {
            AddCommand(OperationCode.LineNum, _parser.CurrentLine, CodeGenerationFlags.CodeStatistics);

            var beginTryIndex = AddCommand(OperationCode.BeginTry, -1);
            bool savedTryFlag = SetTryBlockFlag(true);
            PushStructureToken(Token.Exception);
            NextToken();
            BuildCodeBatch();
            PopStructureToken();
            SetTryBlockFlag(savedTryFlag);
            var jmpIndex = AddCommand(OperationCode.Jmp, -1);

            Assert(_lastExtractedLexem.Token == Token.Exception);
            if (StringComparer.OrdinalIgnoreCase.Compare(_lastExtractedLexem.Content, "Exception") == 0)
                SystemLogger.Write("WARNING! BREAKING CHANGE: Keyword 'Exception' is not supported anymore. Consider using 'Except'");

            var beginHandler = AddCommand(OperationCode.LineNum, _lastExtractedLexem.LineNumber, CodeGenerationFlags.CodeStatistics);

            CorrectCommandArgument(beginTryIndex, beginHandler);

            PushStructureToken(Token.EndTry);
            NextToken();
            BuildCodeBatch();
            PopStructureToken();

            var endIndex = AddCommand(OperationCode.LineNum, _lastExtractedLexem.LineNumber, CodeGenerationFlags.CodeStatistics | CodeGenerationFlags.DebugCode);
            AddCommand(OperationCode.EndTry, 0);
            CorrectCommandArgument(jmpIndex, endIndex);
            
            NextToken();
        }

        private void BuildRaiseExceptionStatement()
        {
            AddCommand(OperationCode.LineNum, _parser.CurrentLine);

            NextToken();
            if (_lastExtractedLexem.Token == Token.Semicolon)
            {
                if (_tokenStack.Any(x => x.Contains(Token.EndTry)))
                {
                    AddCommand(OperationCode.RaiseException, -1);
                }
                else
                {
                    throw CompilerException.MismatchedRaiseException();
                }
            }
            else
            {
                BuildExpression(Token.Semicolon);
                AddCommand(OperationCode.RaiseException, 0);
            }

        }

        private void BuildExecuteStatement()
        {
            AddCommand(OperationCode.LineNum, _parser.CurrentLine);
            NextToken();

            BuildExpression(Token.Semicolon);
            AddCommand(OperationCode.Execute, 0);

        }

        private void CorrectCommandArgument(int index, int newArgument)
        {
            var cmd = _module.Code[index];
            cmd.Argument = newArgument;
            _module.Code[index] = cmd;
        }

        private void CorrectBreakStatements(NestedLoopInfo nestedLoopInfo, int endLoopIndex)
        {
            foreach (var breakCmdIndex in nestedLoopInfo.breakStatements)
            {
                CorrectCommandArgument(breakCmdIndex, endLoopIndex);
            }
        }

        private bool SetTryBlockFlag(bool isInTry)
        {
            bool current = _isInTryBlock;
            _isInTryBlock = isInTry;
            return current;
        }

        private void BuildSimpleStatement()
        {
            var identifier = _lastExtractedLexem.Content;

            NextToken();
            AddCommand(OperationCode.LineNum, _parser.CurrentLine);
            switch (_lastExtractedLexem.Token)
            {
                case Token.Equal:
                    NextToken();
                    BuildExpression(Token.Semicolon);
                    BuildLoadVariable(identifier);
                    break;
                case Token.OpenPar:
                    ProcessCallOnLeftHand(identifier);
                    break;
                case Token.Dot:
                case Token.OpenBracket:
                    // access chain
                    BuildPushVariable(identifier);
                    BuildAccessChainLeftHand();
                    break;
                default:
                    throw CompilerException.UnexpectedOperation();
            }
        }

        private void ProcessCallOnLeftHand(string identifier)
        {
            var args = PushMethodArgumentsBeforeCall();
            if(IsContinuationToken(ref _lastExtractedLexem))
            {
                BuildMethodCall(identifier, args, true);
                BuildAccessChainLeftHand();
            }
            else
            {
                BuildMethodCall(identifier, args, false);
            }

        }

        private void BuildAccessChainLeftHand()
        {
            string ident;
            BuildContinuationLeftHand(out ident);

            if (ident == null)
            {
                // это присваивание
                if (_lastExtractedLexem.Token != Token.Equal)
                    throw CompilerException.UnexpectedOperation();

                NextToken(); // перешли к выражению
                BuildExpression(Token.Semicolon);
                AddCommand(OperationCode.AssignRef, 0);

            }
            else
            {
                // это вызов
                System.Diagnostics.Debug.Assert(_lastExtractedLexem.Token == Token.OpenPar);
                PushMethodArgumentsBeforeCall();
                var cDef = new ConstDefinition();
                cDef.Type = DataType.String;
                cDef.Presentation = ident;
                int lastIdentifierConst = GetConstNumber(ref cDef);

                if (IsContinuationToken(ref _lastExtractedLexem))
                {
                    AddCommand(OperationCode.ResolveMethodFunc, lastIdentifierConst);
                    BuildAccessChainLeftHand();
                }
                else
                {
                    AddCommand(OperationCode.ResolveMethodProc, lastIdentifierConst);
                }

            }
        }

        private bool IsContinuationToken(ref Lexem lex)
        {
            return lex.Token == Token.Dot || lex.Token == Token.OpenBracket;
        }

        private void BuildExpression(Token stopToken)
        {
            BuildPrimaryNode();
            BuildOperation(0);
            if (_lastExtractedLexem.Token == stopToken)
                return;

            var endTokens = _tokenStack.Peek();

            if (endTokens.Contains(_lastExtractedLexem.Token))
                return;
            else if (_lastExtractedLexem.Token == Token.EndOfText)
                throw CompilerException.UnexpectedEndOfText();
            else
                throw CompilerException.ExpressionSyntax();
        }

        private void BuildOperation(int acceptablePriority)
        {
            var currentOp = _lastExtractedLexem.Token;
            var opPriority = GetBinaryPriority(currentOp);
            while (LanguageDef.IsBinaryOperator(currentOp) && opPriority >= acceptablePriority)
            {
                bool isLogical = LanguageDef.IsLogicalOperator(currentOp);
                int logicalCmdIndex = -1;

                if (isLogical)
                {
                    logicalCmdIndex = AddCommand(TokenToOperationCode(currentOp), 0);
                }

                NextToken();
                BuildPrimaryNode();

                var newOp = _lastExtractedLexem.Token;
                int newPriority = GetBinaryPriority(newOp);

                if (newPriority > opPriority)
                {
                    BuildOperation(newPriority);
                }

                if (isLogical)
                {
                    currentOp = _lastExtractedLexem.Token;
                    newPriority = GetBinaryPriority(currentOp);
                    if(opPriority < newPriority)
                    {
                        BuildOperation(newPriority);
                    }

                    AddCommand(OperationCode.MakeBool, 0);
                    CorrectCommandArgument(logicalCmdIndex, _module.Code.Count);
                }
                else
                {
                    var opCode = TokenToOperationCode(currentOp);
                    AddCommand(opCode, 0);
                }

                currentOp = _lastExtractedLexem.Token;
                opPriority = GetBinaryPriority(currentOp);

            }
        }

        private static OperationCode TokenToOperationCode(Token stackOp)
        {
            OperationCode opCode;
            switch (stackOp)
            {
                case Token.Equal:
                    opCode = OperationCode.Equals;
                    break;
                case Token.NotEqual:
                    opCode = OperationCode.NotEqual;
                    break;
                case Token.Plus:
                    opCode = OperationCode.Add;
                    break;
                case Token.Minus:
                    opCode = OperationCode.Sub;
                    break;
                case Token.Multiply:
                    opCode = OperationCode.Mul;
                    break;
                case Token.Division:
                    opCode = OperationCode.Div;
                    break;
                case Token.Modulo:
                    opCode = OperationCode.Mod;
                    break;
                case Token.UnaryPlus:
                    opCode = OperationCode.Number;
                    break;
                case Token.UnaryMinus:
                    opCode = OperationCode.Neg;
                    break;
                case Token.And:
                    opCode = OperationCode.And;
                    break;
                case Token.Or:
                    opCode = OperationCode.Or;
                    break;
                case Token.Not:
                    opCode = OperationCode.Not;
                    break;
                case Token.LessThan:
                    opCode = OperationCode.Less;
                    break;
                case Token.LessOrEqual:
                    opCode = OperationCode.LessOrEqual;
                    break;
                case Token.MoreThan:
                    opCode = OperationCode.Greater;
                    break;
                case Token.MoreOrEqual:
                    opCode = OperationCode.GreaterOrEqual;
                    break;
                default:
                    throw new NotSupportedException();
            }
            return opCode;
        }

        private static int GetBinaryPriority(Token newOp)
        {
            int newPriority;
            if (LanguageDef.IsBinaryOperator(newOp))
                newPriority = LanguageDef.GetPriority(newOp);
            else
                newPriority = -1;

            return newPriority;
        }

        private void BuildPrimaryNode()
        {
            if (LanguageDef.IsLiteral(ref _lastExtractedLexem))
            {
                BuildPushConstant();
                NextToken();
            }
            else if (LanguageDef.IsUserSymbol(ref _lastExtractedLexem))
            {
                ProcessPrimaryIdentifier();
            }
            else if (_lastExtractedLexem.Token == Token.Plus)
            {
                ProcessPrimaryUnaryPlus();
            }
            else if (_lastExtractedLexem.Token == Token.Minus)
            {
                ProcessPrimaryUnaryMinus();
            }
            else if (_lastExtractedLexem.Token == Token.Not)
            {
                ProcessUnaryBoolean();
            }
            else if (_lastExtractedLexem.Token == Token.OpenPar)
            {
                ProcessSubexpression();
                BuildContinuationRightHand();
            }
            else if(_lastExtractedLexem.Token == Token.NewObject)
            {
                BuildNewObjectCreation();
                BuildContinuationRightHand();
            }
            else if (LanguageDef.IsBuiltInFunction(_lastExtractedLexem.Token))
            {
                BuildBuiltinFunction();
                BuildContinuationRightHand();
            }
            else if (_lastExtractedLexem.Token == Token.Question)
            {
                BuildQuestionOperator();
                BuildContinuationRightHand();
            }
            else
            {
                throw CompilerException.ExpressionSyntax();
            }
        }
        private void ProcessPrimaryIdentifier()
        {
            var identifier = _lastExtractedLexem.Content;
            var lineNumber = _lastExtractedLexem.LineNumber;
            NextToken();
            if (IsContinuationToken(ref _lastExtractedLexem))
            {
                BuildPushVariable(identifier);
                BuildContinuationRightHand();
            }
            else if (_lastExtractedLexem.Token == Token.OpenPar)
            {
                BuildFunctionCall(identifier, lineNumber);
                BuildContinuationRightHand();
            }
            else
            {
                BuildPushVariable(identifier);
            }
        }

        private bool LastExtractedIsPimary()
        {
            return LanguageDef.IsLiteral(ref _lastExtractedLexem)
                || LanguageDef.IsIdentifier(ref _lastExtractedLexem)
                || _lastExtractedLexem.Token == Token.OpenPar;
        }

        private void ProcessPrimaryUnaryMinus()
        {
            NextToken();
            if (!LastExtractedIsPimary())
            {
                throw CompilerException.ExpressionExpected();
            }

            BuildPrimaryNode();
            AddCommand(OperationCode.Neg, 0);
        }

        private void ProcessPrimaryUnaryPlus()
        {
            NextToken();
            if (!LastExtractedIsPimary())
            {
                throw CompilerException.ExpressionExpected();
            }

            BuildPrimaryNode();
            AddCommand(OperationCode.Number, 0);
        }

        private void ProcessSubexpression()
        {
            NextToken(); // съели открывающую скобку
            BuildPrimaryNode();
            BuildOperation(0);

            if (_lastExtractedLexem.Token != Token.ClosePar)
                throw CompilerException.TokenExpected(")");

            NextToken(); // съели закрывающую скобку
        }

        private void ProcessUnaryBoolean()
        {
            NextToken();
            BuildPrimaryNode();
            BuildOperation(GetBinaryPriority(Token.Not));
            AddCommand(OperationCode.Not, 0);
        }

        private void BuildContinuationRightHand()
        {
            string dummy;
            BuildContinuationInternal(false, out dummy);
        }

        private void BuildContinuationLeftHand(out string lastIdentifier)
        {
            BuildContinuationInternal(true, out lastIdentifier);
        }

        private void BuildContinuationInternal(bool interruptOnCall, out string lastIdentifier)
        {
            lastIdentifier = null;
            while (true)
            {
                if (_lastExtractedLexem.Token == Token.Dot)
                {
                    NextToken();
                    if (!IsValidPropertyName(ref _lastExtractedLexem))
                        throw CompilerException.IdentifierExpected();

                    string identifier = _lastExtractedLexem.Content;
                    NextToken();
                    if (_lastExtractedLexem.Token == Token.OpenPar)
                    {
                        if (interruptOnCall)
                        {
                            lastIdentifier = identifier;
                            return;
                        }
                        else
                        {
                            var args = BuildArgumentList();
                            var cDef = new ConstDefinition();
                            cDef.Type = DataType.String;
                            cDef.Presentation = identifier;
                            int lastIdentifierConst = GetConstNumber(ref cDef);
                            AddCommand(OperationCode.ArgNum, args.Length);
                            AddCommand(OperationCode.ResolveMethodFunc, lastIdentifierConst);
                        }
                    }
                    else
                    {
                        ResolveProperty(identifier);
                    }
                }
                else if (_lastExtractedLexem.Token == Token.OpenBracket)
                {
                    NextToken();
                    if (_lastExtractedLexem.Token == Token.CloseBracket)
                        throw CompilerException.ExpressionExpected();

                    BuildExpression(Token.CloseBracket);
                    System.Diagnostics.Debug.Assert(_lastExtractedLexem.Token == Token.CloseBracket);
                    NextToken();

                    AddCommand(OperationCode.PushIndexed, 0);
                }
                else
                {
                    break;
                }
            }

        }

        private bool IsValidPropertyName(ref Lexem lex)
        {
            return LanguageDef.IsIdentifier(ref lex) 
                || lex.Type == LexemType.BooleanLiteral
                || lex.Type == LexemType.NullLiteral
                || lex.Type == LexemType.UndefinedLiteral
                || lex.Token == Token.And
                || lex.Token == Token.Or
                || lex.Token == Token.Not;
        }

        private void ResolveProperty(string identifier)
        {
            var cDef = new ConstDefinition();
            cDef.Type = DataType.String;
            cDef.Presentation = identifier;
            var identifierConstIndex = GetConstNumber(ref cDef);
            AddCommand(OperationCode.ResolveProp, identifierConstIndex);
        }

        private void BuildQuestionOperator()
        {
            Assert(_lastExtractedLexem.Token == Token.Question);
            NextToken();
            if (_lastExtractedLexem.Token != Token.OpenPar)
                throw CompilerException.UnexpectedOperation();

            NextToken();
            BuildExpression(Token.Comma);
            if (_lastExtractedLexem.Token != Token.Comma)
                throw CompilerException.UnexpectedOperation();
            
            AddCommand(OperationCode.MakeBool, 0);
            var addrOfCondition = AddCommand(OperationCode.JmpFalse, -1);

            NextToken();
            BuildExpression(Token.Comma); // построили true-part
            if (_lastExtractedLexem.Token != Token.Comma)
                throw CompilerException.UnexpectedOperation();

            var endOfTruePart = AddCommand(OperationCode.Jmp, -1); // уход в конец оператора
            
            CorrectCommandArgument(addrOfCondition, AddCommand(OperationCode.Nop, 0)); // отметили, куда переходить по false
            NextToken();
            BuildExpression(Token.ClosePar); // построили false-part
            
            var endOfFalsePart = AddCommand(OperationCode.Nop, 0);
            CorrectCommandArgument(endOfTruePart, endOfFalsePart);
            
            NextToken();

        }

        private bool[] BuildArgumentList()
        {
            System.Diagnostics.Debug.Assert(_lastExtractedLexem.Token == Token.OpenPar);

            PushStructureToken(Token.ClosePar);
            List<bool> arguments = new List<bool>();

            try
            {
                NextToken(); // съели открывающую скобку
                while (_lastExtractedLexem.Token != Token.ClosePar)
                {
                    PushPassedArgument(arguments);
                }

                if (_lastExtractedLexem.Token != Token.ClosePar)
                    throw CompilerException.TokenExpected(")");

                NextToken(); // съели закрывающую скобку
            }
            finally
            {
                PopStructureToken();
            }
            
            return arguments.ToArray();
        }

        private void PushPassedArgument(IList<bool> arguments)
        {
            if (_lastExtractedLexem.Token == Token.Comma)
            {
                AddCommand(OperationCode.PushDefaultArg, 0);
                arguments.Add(false);
                NextToken();
                if (_lastExtractedLexem.Token == Token.ClosePar)
                {
                    AddCommand(OperationCode.PushDefaultArg, 0);
                    arguments.Add(false);
                }
            }
            else if (_lastExtractedLexem.Token != Token.ClosePar)
            {
                BuildExpression(Token.Comma);
                arguments.Add(true);
                if (_lastExtractedLexem.Token == Token.Comma)
                {
                    NextToken();
                    if (_lastExtractedLexem.Token == Token.ClosePar)
                    {
                        AddCommand(OperationCode.PushDefaultArg, 0);
                        arguments.Add(false);
                    }
                }
            }
        }

        private void BuildLoadVariable(string identifier)
        {
            var hasVar = _ctx.TryGetVariable(identifier, out var varBinding);
            if (hasVar)
            {
                if (varBinding.binding.ContextIndex == _ctx.TopIndex())
                {
                    AddCommand(OperationCode.LoadLoc, varBinding.binding.CodeIndex);
                }
                else
                {
                    var num = GetVariableRefNumber(ref varBinding.binding);
                    AddCommand(OperationCode.LoadVar, num);
                }
            }
            else
            {
                // can create variable
                var binding = _ctx.DefineVariable(identifier);
                AddCommand(OperationCode.LoadLoc, binding.CodeIndex);
            }
        }
        
        private void BuildFunctionCall(string identifier, int callLineNumber)
        {
            bool[] args = PushMethodArgumentsBeforeCall();
            AddCommand(OperationCode.LineNum, callLineNumber, CodeGenerationFlags.CodeStatistics);
            BuildMethodCall(identifier, args, true);
            AddCommand(OperationCode.LineNum, callLineNumber, CodeGenerationFlags.DebugCode);
        }

        private bool[] PushMethodArgumentsBeforeCall()
        {
            var argsPassed = BuildArgumentList();
            AddCommand(OperationCode.ArgNum, argsPassed.Length);
            return argsPassed;
        }

        private void BuildMethodCall(string identifier, bool[] argsPassed, bool asFunction)
        {
            var hasMethod = _ctx.TryGetMethod(identifier, out var methBinding);
            if (hasMethod)
            {
                var scope = _ctx.GetScope(methBinding.ContextIndex);

                // dynamic scope checks signatures only at runtime
                if (!scope.IsDynamicScope)
                {
                    var methInfo = scope.GetMethod(methBinding.CodeIndex);
                    if (asFunction && !methInfo.IsFunction)
                    {
                        throw CompilerException.UseProcAsFunction();
                    }
                    CheckFactArguments(methInfo, argsPassed);
                }

                if (asFunction)
                    AddCommand(OperationCode.CallFunc, GetMethodRefNumber(ref methBinding));
                else
                    AddCommand(OperationCode.CallProc, GetMethodRefNumber(ref methBinding)); 
            }
            else
            {
                // can be defined later
                var forwarded = new ForwardedMethodDecl();
                forwarded.identifier = identifier;
                forwarded.asFunction = asFunction;
                forwarded.codeLine = _parser.CurrentLine;
                forwarded.factArguments = argsPassed;

                var opCode = asFunction ? OperationCode.CallFunc : OperationCode.CallProc;
                forwarded.commandIndex = AddCommand(opCode, -1);
                _forwardedMethods.Add(forwarded);
            }
        }

        private void CheckFactArguments(MethodInfo methInfo, bool[] argsPassed)
        {
            CheckFactArguments(methInfo.Params, argsPassed);
        }

        private void CheckFactArguments(ParameterDefinition[] parameters, bool[] argsPassed)
        {
            if (argsPassed.Length > parameters.Length)
            {
                throw CompilerException.TooManyArgumentsPassed();
            }

            if (parameters.Skip(argsPassed.Length).Any(param => !param.HasDefaultValue))
            {
                throw CompilerException.TooLittleArgumentsPassed();
            }
        }

        private void BuildNewObjectCreation()
        {
            NextToken();
            if (_lastExtractedLexem.Token == Token.OpenPar)
            {
                // создание по строковому имени класса
                NewObjectDynamicConstructor();

            }
            else if (IsUserSymbol(ref _lastExtractedLexem) || _lastExtractedLexem.Token == Token.ExceptionInfo)
            {
                NewObjectStaticConstructor();
            }
            else
            {
                throw CompilerException.IdentifierExpected();
            }

        }

        private void NewObjectDynamicConstructor()
        {
            var argsPassed = BuildArgumentList();
            if (argsPassed.Length == 0)
                throw CompilerException.ExpressionExpected();

            AddCommand(OperationCode.NewInstance, argsPassed.Length-1);
        }

        private void NewObjectStaticConstructor()
        {
            var name = _lastExtractedLexem.Content;
            var cDef = new ConstDefinition()
            {
                Type = DataType.String,
                Presentation = name
            };

            AddCommand(OperationCode.PushConst, GetConstNumber(ref cDef));

            NextToken();
            bool[] argsPassed;
            if (_lastExtractedLexem.Token == Token.OpenPar)
            {
                // Отрабатываем только в тех случаях, если явно указана скобка.
                // В остальных случаях дальнейшую обработку отдаём наружу
                argsPassed = BuildArgumentList();
            }
            else
            {
                argsPassed = new bool[0];
            }

            AddCommand(OperationCode.NewInstance, argsPassed.Length);
        }

        private void BuildPushConstant()
        {
            var cDef = CreateConstDefinition(ref _lastExtractedLexem);
            var num = GetConstNumber(ref cDef);
            AddCommand(OperationCode.PushConst, num);
        }

        private int BuildPushVariable(string identifier)
        {
            var varNum = _ctx.GetVariable(identifier);
            if (varNum.type == SymbolType.ContextProperty)
            {
                return PushReference(varNum.binding);
            }
            else
            {
                return PushSimpleVariable(varNum.binding);
            }
        }

        private void BuildBuiltinFunction()
        {
            OperationCode funcId = BuiltInFunctionCode(_lastExtractedLexem.Token);
            NextToken();
            if(_lastExtractedLexem.Token != Token.OpenPar)
            {
                throw CompilerException.TokenExpected(Token.OpenPar);
            }

            var passedArgs = BuildArgumentList();
            if (funcId == OperationCode.Min || funcId == OperationCode.Max)
            {
                if (passedArgs.Length == 0)
                    throw CompilerException.TooLittleArgumentsPassed();
            }
            else
            {
                var parameters = BuiltinFunctions.ParametersInfo(funcId);
                CheckFactArguments(parameters, passedArgs);
            }

            AddCommand(funcId, passedArgs.Length);

        }

        #region Helper methods

        private static bool IsUserSymbol(ref Lexem lex)
        {
            return LanguageDef.IsUserSymbol(ref lex);
        }

        private static bool IsLiteral(ref Lexem lex)
        {
            return LanguageDef.IsLiteral(ref lex);
        }

        private static OperationCode BuiltInFunctionCode(Token token)
        {
            return _tokenToOpCode[token];
        }

        private static ConstDefinition CreateConstDefinition(ref Lexem lex)
        {
            DataType constType = DataType.Undefined;
            switch (lex.Type)
            {
                case LexemType.BooleanLiteral:
                    constType = DataType.Boolean;
                    break;
                case LexemType.DateLiteral:
                    constType = DataType.Date;
                    break;
                case LexemType.NumberLiteral:
                    constType = DataType.Number;
                    break;
                case LexemType.StringLiteral:
                    constType = DataType.String;
                    break;
                case LexemType.NullLiteral:
                    constType = DataType.GenericValue;
                    break;
            }

            ConstDefinition cDef = new ConstDefinition()
            {
                Type = constType,
                Presentation = lex.Content
            };
            return cDef;
        }

        private int GetConstNumber(ref ConstDefinition cDef)
        {
            var idx = _module.Constants.IndexOf(cDef);
            if (idx < 0)
            {
                idx = _module.Constants.Count;
                _module.Constants.Add(cDef);
            }
            return idx;
        }

        private int GetMethodRefNumber(ref SymbolBinding methodBinding)
        {
            var idx = _module.MethodRefs.IndexOf(methodBinding);
            if (idx < 0)
            {
                idx = _module.MethodRefs.Count;
                _module.MethodRefs.Add(methodBinding);
            }
            return idx;
        }

        private int GetVariableRefNumber(ref SymbolBinding binding)
        {
            var idx = _module.VariableRefs.IndexOf(binding);
            if (idx < 0)
            {
                idx = _module.VariableRefs.Count;
                _module.VariableRefs.Add(binding);
            }

            return idx;
        }

        private void NextToken()
        {
            if (_lastExtractedLexem.Token != Token.EndOfText)
            {
                _previousExtractedLexem = _lastExtractedLexem;
                _lastExtractedLexem = _parser.NextLexem();
            }
            else
            {
                throw CompilerException.UnexpectedEndOfText();
            }
        }

        private void PushStructureToken(params Token[] tok)
        {
            _tokenStack.Push(tok);
        }

        private Token[] PopStructureToken()
        {
            var tok = _tokenStack.Pop();
            return tok;
        }

        private int AddCommand(OperationCode code, int arg, CodeGenerationFlags emitConditions = CodeGenerationFlags.Always)
        {
            var addr = _module.Code.Count;
            bool emit = emitConditions == CodeGenerationFlags.Always || ExtraCodeConditionsMet(emitConditions);
            if (emit)
            {
                _module.Code.Add(new Command() { Code = code, Argument = arg });
            }
            return addr;
        }

        private bool ExtraCodeConditionsMet(CodeGenerationFlags emitConditions)
        {
            return (((int)ProduceExtraCode) & (int)emitConditions) != 0;
        }

        private int PushSimpleVariable(SymbolBinding binding)
        {
            if (binding.ContextIndex == _ctx.TopIndex())
            {
                return AddCommand(OperationCode.PushLoc, binding.CodeIndex);
            }
            else
            {
                var idx = GetVariableRefNumber(ref binding);
                return AddCommand(OperationCode.PushVar, idx);
            }
        }

        private int PushReference(SymbolBinding binding)
        {
            var idx = GetVariableRefNumber(ref binding);

            return AddCommand(OperationCode.PushRef, idx);
        }

        #endregion

        [System.Diagnostics.Conditional("DEBUG")]
        private static void Assert(bool condition)
        {
            System.Diagnostics.Debug.Assert(condition);
        }

        static Compiler()
        {
            _tokenToOpCode = new Dictionary<Token, OperationCode>();

            var tokens  = LanguageDef.BuiltInFunctions();
            var opCodes = BuiltinFunctions.GetOperationCodes();

            Assert(tokens.Length == opCodes.Length);
            for (int i = 0; i < tokens.Length; i++)
            {
                _tokenToOpCode.Add(tokens[i], opCodes[i]);
            }
        }

    }

    public delegate bool CompilerDirectiveHandler(string directive, string value, bool codeEntered);

}
