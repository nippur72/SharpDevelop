// <file>
//     <copyright see="prj:///doc/copyright.txt"/>
//     <license see="prj:///doc/license.txt"/>
//     <owner name="Christian Hornung" email=""/>
//     <version>$Revision$</version>
// </file>

using System;
using System.Collections.Generic;
using Hornung.ResourceToolkit.ResourceFileContent;
using ICSharpCode.Core;
using ICSharpCode.NRefactory.Ast;
using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.Dom;
using ICSharpCode.SharpDevelop.Project;
using ICSharpCode.NRefactory;

namespace Hornung.ResourceToolkit.Resolver
{
	/// <summary>
	/// Detects and resolves resource references using the standard .NET
	/// framework provided resource access methods (ResourceManager and derived
	/// classes).
	/// </summary>
	public class BclNRefactoryResourceResolver : INRefactoryResourceResolver
	{
		
		/// <summary>
		/// Tries to find a resource reference in the specified expression.
		/// </summary>
		/// <param name="expressionResult">The ExpressionResult for the expression.</param>
		/// <param name="expr">The AST representation of the full expression.</param>
		/// <param name="resolveResult">SharpDevelop's ResolveResult for the expression.</param>
		/// <param name="caretLine">The line where the expression is located.</param>
		/// <param name="caretColumn">The column where the expression is located.</param>
		/// <param name="fileName">The name of the source file where the expression is located.</param>
		/// <param name="fileContent">The content of the source file where the expression is located.</param>
		/// <param name="expressionFinder">The ExpressionFinder for the file.</param>
		/// <param name="charTyped">The character that has been typed at the caret position but is not yet in the buffer (this is used when invoked from code completion), or <c>null</c>.</param>
		/// <returns>A ResourceResolveResult describing the referenced resource, or <c>null</c>, if this expression does not reference a resource using the standard .NET framework classes.</returns>
		public ResourceResolveResult Resolve(ExpressionResult expressionResult, Expression expr, ResolveResult resolveResult, int caretLine, int caretColumn, string fileName, string fileContent, IExpressionFinder expressionFinder, char? charTyped)
		{
			/*
			 * We need to catch the following cases here:
			 * 
			 * Something.GetString(
			 * Something.GetString("...")
			 * Something[
			 * Something["..."]
			 * 
			 */
			
			if (charTyped == '(') {
				
				// Something.GetString
				// This is a MethodResolveResult and we need the reference to "Something",
				// which is the next outer expression.
				// This is only valid when invoked from code completion
				// and the method invocation character ('(' in C# and VB)
				// has been typed.
				
				// This code is also reused when reducing a complete InvocationExpression
				// (MemberResolveResult) to the method reference by passing '(' as
				// charTyped explicitly.
				
				MethodResolveResult methrr = resolveResult as MethodResolveResult;
				if (methrr != null) {
					if ((methrr.Name == "GetString" || methrr.Name == "GetObject" || methrr.Name == "GetStream") &&
					    (resolveResult = NRefactoryAstCacheService.ResolveNextOuterExpression(ref expressionResult, caretLine, caretColumn, fileName, expressionFinder)) != null) {
						
						return ResolveResource(resolveResult, expr);
						
					} else {
						
						return null;
						
					}
				}
				
			}
			
			// Do not use "else if" here.
			// '(' is also the IndexerExpressionStartToken for VB,
			// so the "else" block further down might still apply.
			
			if (charTyped == null) {
				
				// A MemberResolveResult with a complete expression
				// must only be considered a valid resource reference
				// when Resolve is not invoked from code completion
				// (i.e. charTyped == null) because this indicates
				// that the resource reference is already before the typed character
				// and we are not interested in the following expression.
				// This may happen when typing something like:
				// Something.GetString("...")[
				
				MemberResolveResult mrr = resolveResult as MemberResolveResult;
				if (mrr != null) {
					
					if (mrr.ResolvedMember is IMethod &&
					    (mrr.ResolvedMember.Name == "GetString" || mrr.ResolvedMember.Name == "GetObject" || mrr.ResolvedMember.Name == "GetStream")) {
						
						// Something.GetString("...")
						// This is a MemberResolveResult and we need the reference to "Something".
						// The expression finder may only remove the string literal, so
						// we have to call Resolve again in this case to resolve
						// the method reference.
						
						if ((resolveResult = NRefactoryAstCacheService.ResolveNextOuterExpression(ref expressionResult, caretLine, caretColumn, fileName, expressionFinder)) != null) {
							
							if (resolveResult is MethodResolveResult) {
								return this.Resolve(expressionResult, expr, resolveResult, caretLine, caretColumn, fileName, fileContent, expressionFinder, '(');
							} else {
								return ResolveResource(resolveResult, expr);
							}
							
						} else {
							
							return null;
							
						}
						
					} else if (expr is IndexerExpression &&
					           IsResourceManager(mrr.ResolvedMember.DeclaringType.DefaultReturnType, fileName)) {
						
						// Something["..."] is an IndexerExpression.
						// We need the reference to Something and this is
						// the next outer expression.
						
						if ((resolveResult = NRefactoryAstCacheService.ResolveNextOuterExpression(ref expressionResult, caretLine, caretColumn, fileName, expressionFinder)) != null) {
							return ResolveResource(resolveResult, expr);
						} else {
							return null;
						}
						
					}
					
				}
				
			} else {
				
				// This request is triggered from code completion.
				// The only case that has not been caught above is:
				// Something[
				// The reference to "Something" is already in this expression.
				// So we have to test the trigger character against the
				// indexer expression start token of the file's language.
				
				LanguageProperties lp = NRefactoryResourceResolver.GetLanguagePropertiesForFile(fileName);
				if (lp != null &&
				    !String.IsNullOrEmpty(lp.IndexerExpressionStartToken) &&
				    lp.IndexerExpressionStartToken[0] == charTyped) {
					
					#if DEBUG
					LoggingService.Debug("ResourceToolkit: BclNRefactoryResourceResolver: Indexer expression start typed, ResolveResult: "+resolveResult.ToString());
					LoggingService.Debug("ResourceToolkit: BclNRefactoryResourceResolver: -> Expression: "+expr.ToString());
					#endif
					
					return ResolveResource(resolveResult, expr);
					
				}
				
			}
			
			return null;
		}
		
		// ********************************************************************************************************************************
		
		#region ResourceFileContent mapping cache
		
		static Dictionary<IMember, IResourceFileContent> cachedResourceFileContentMappings;
		
		static BclNRefactoryResourceResolver()
		{
			cachedResourceFileContentMappings = new Dictionary<IMember, IResourceFileContent>(new MemberEqualityComparer());
			NRefactoryAstCacheService.CacheEnabledChanged += NRefactoryCacheEnabledChanged;
		}
		
		static void NRefactoryCacheEnabledChanged(object sender, EventArgs e)
		{
			if (!NRefactoryAstCacheService.CacheEnabled) {
				// Clear cache when disabled.
				cachedResourceFileContentMappings.Clear();
			}
		}
		
		#endregion
		
		/// <summary>
		/// Tries to find a resource reference in the specified expression.
		/// </summary>
		/// <param name="resolveResult">The ResolveResult that describes the referenced member.</param>
		/// <param name="expr">The AST representation of the full expression.</param>
		/// <returns>
		/// The ResourceResolveResult describing the referenced resource, if successful, 
		/// or a null reference, if the referenced member is not a resource manager 
		/// or if the resource file cannot be determined.
		/// </returns>
		static ResourceResolveResult ResolveResource(ResolveResult resolveResult, Expression expr)
		{
			IResourceFileContent rfc = null;
			
			MemberResolveResult mrr = resolveResult as MemberResolveResult;
			if (mrr != null) {
				rfc = ResolveResourceFileContent(mrr.ResolvedMember);
			} else {
				
				LocalResolveResult lrr = resolveResult as LocalResolveResult;
				if (lrr != null) {
					if (!lrr.IsParameter) {
						rfc = ResolveResourceFileContent(lrr.Field);
					}
				}
				
			}
			
			if (rfc != null) {
				string key = GetKeyFromExpression(expr);
				
				// TODO: Add information about return type (of the resource, if present).
				return new ResourceResolveResult(resolveResult.CallingClass, resolveResult.CallingMember, null, rfc, key);
			}
			
			return null;
		}
		
		/// <summary>
		/// Tries to determine the resource file content which is referenced by the
		/// resource manager which is assigned to the specified member.
		/// </summary>
		/// <returns>
		/// The IResourceFileContent, if successful, or a null reference, if the
		/// specified member is not a resource manager or if the
		/// resource file cannot be determined.
		/// </returns>
		static IResourceFileContent ResolveResourceFileContent(IMember member)
		{
			if (member != null && member.ReturnType != null &&
			    member.DeclaringType != null && member.DeclaringType.CompilationUnit != null) {
				
				IResourceFileContent content;
				if (!NRefactoryAstCacheService.CacheEnabled || !cachedResourceFileContentMappings.TryGetValue(member, out content)) {
					
					string declaringFileName = member.DeclaringType.CompilationUnit.FileName;
					if (declaringFileName != null) {
						if (IsResourceManager(member.ReturnType, declaringFileName)) {
							
							SupportedLanguage? language = NRefactoryResourceResolver.GetFileLanguage(declaringFileName);
							if (language == null) {
								return null;
							}
							
							CompilationUnit cu = NRefactoryAstCacheService.GetFullAst(language.Value, declaringFileName);
							if (cu != null) {
								
								ResourceManagerInitializationFindVisitor visitor = new ResourceManagerInitializationFindVisitor(member);
								cu.AcceptVisitor(visitor, null);
								if (visitor.FoundFileName != null) {
									
									content = ResourceFileContentRegistry.GetResourceFileContent(visitor.FoundFileName);
									
									if (NRefactoryAstCacheService.CacheEnabled && content != null) {
										cachedResourceFileContentMappings.Add(member, content);
									}
									
									return content;
									
								}
								
							}
							
						}
					}
					
					return null;
					
				}
				
				return content;
				
			}
			return null;
		}
		
		/// <summary>
		/// Determines if the specified type is a ResourceManager type that can
		/// be handled by this resolver.
		/// </summary>
		/// <param name="type">The type that will be checked if it is a ResourceManager.</param>
		/// <param name="sourceFileName">The name of the source code file where the reference to this type occurs.</param>
		static bool IsResourceManager(IReturnType type, string sourceFileName)
		{
			IProject p = ProjectFileDictionaryService.GetProjectForFile(sourceFileName);
			IProjectContent pc;
			if (p == null) {
				pc = ParserService.CurrentProjectContent;
			} else {
				pc = ParserService.GetProjectContent(p);
			}
			
			if (pc == null) {
				return false;
			}
			
			IClass c = type.GetUnderlyingClass();
			if (c == null) {
				return false;
			}
			
			IClass resourceManager = pc.GetClass("System.Resources.ResourceManager");
			if (resourceManager == null) {
				return false;
			}
			
			return (c.CompareTo(resourceManager) == 0 || c.IsTypeInInheritanceTree(resourceManager));
		}
		
		// ********************************************************************************************************************************
		
		#region ResourceManagerInitializationFindVisitor
		
		/// <summary>
		/// Finds an initialization statement for a resource manager member and
		/// tries to infer the referenced resource file from the parameters
		/// given to the resource manager constructor.
		/// </summary>
		class ResourceManagerInitializationFindVisitor : PositionTrackingAstVisitor
		{
			
			readonly IMember resourceManagerMember;
			readonly bool isLocalVariable;
			
			bool triedToResolvePropertyAssociation;
			IField resourceManagerFieldAccessedByProperty;
			
			CompilationUnit compilationUnit;
			
			string foundFileName;
			
			/// <summary>
			/// Gets the resource file name which the resource manager accesses, or a null reference if the file could not be determined or does not exist.
			/// </summary>
			public string FoundFileName {
				get {
					return this.foundFileName;
				}
			}
			
			/// <summary>
			/// Initializes a new instance of the <see cref="ResourceManagerInitializationFindVisitor" /> class.
			/// </summary>
			/// <param name="resourceManagerMember">The member which the resource manager to be found is assigned to.</param>
			public ResourceManagerInitializationFindVisitor(IMember resourceManagerMember) : base()
			{
				this.resourceManagerMember = resourceManagerMember;
				IField resourceManagerField = resourceManagerMember as IField;
				if (resourceManagerField != null && resourceManagerField.IsLocalVariable) {
					this.isLocalVariable = true;
				}
			}
			
			public override object TrackedVisit(CompilationUnit compilationUnit, object data)
			{
				this.compilationUnit = compilationUnit;
				return base.TrackedVisit(compilationUnit, data);
			}
			
			public override object TrackedVisit(LocalVariableDeclaration localVariableDeclaration, object data)
			{
				return base.TrackedVisit(localVariableDeclaration, localVariableDeclaration);
			}
			
			public override object TrackedVisit(FieldDeclaration fieldDeclaration, object data)
			{
				return base.TrackedVisit(fieldDeclaration, fieldDeclaration);
			}
			
			public override object TrackedVisit(VariableDeclaration variableDeclaration, object data)
			{
				// Resolving anything here only makes sense
				// if this declaration actually has an initializer.
				if (variableDeclaration.Initializer.IsNull) {
					return base.TrackedVisit(variableDeclaration, data);
				}
				
				LocalVariableDeclaration localVariableDeclaration = data as LocalVariableDeclaration;
				if (this.isLocalVariable && localVariableDeclaration != null) {
					if (variableDeclaration.Name == this.resourceManagerMember.Name) {
						// Make sure we got the right declaration by comparing the positions.
						// Both must have the same start position.
						if (localVariableDeclaration.StartLocation.X == this.resourceManagerMember.Region.BeginColumn && localVariableDeclaration.StartLocation.Y == this.resourceManagerMember.Region.BeginLine) {
							#if DEBUG
							LoggingService.Debug("ResourceToolkit: BclNRefactoryResourceResolver found local variable declaration: "+localVariableDeclaration.ToString()+" at "+localVariableDeclaration.StartLocation.ToString());
							#endif
							data = true;
						}
						
					}
				}
				
				FieldDeclaration fieldDeclaration = data as FieldDeclaration;
				if (!this.isLocalVariable && fieldDeclaration != null) {
					// Make sure we got the right declaration by comparing the positions.
					// Both must have the same start position.
					if (variableDeclaration.Name == this.resourceManagerMember.Name &&
					    fieldDeclaration.StartLocation.X == this.resourceManagerMember.Region.BeginColumn && fieldDeclaration.StartLocation.Y == this.resourceManagerMember.Region.BeginLine) {
						
						#if DEBUG
						LoggingService.Debug("ResourceToolkit: BclNRefactoryResourceResolver found field declaration: "+fieldDeclaration.ToString()+" at "+fieldDeclaration.StartLocation.ToString());
						#endif
						data = true;
						
					} else {
						
						// This field might be referred to by a property
						// that we are looking for.
						// This association is cached in the
						// resourceManagerFieldAccessedByProperty field
						// to improve performance.
						this.TryResolveResourceManagerProperty();
						
						if (this.resourceManagerFieldAccessedByProperty != null &&
						    fieldDeclaration.StartLocation.X == this.resourceManagerFieldAccessedByProperty.Region.BeginColumn &&
						    fieldDeclaration.StartLocation.Y == this.resourceManagerFieldAccessedByProperty.Region.BeginLine) {
							
							#if DEBUG
							LoggingService.Debug("ResourceToolkit: BclNRefactoryResourceResolver found field declaration (via associated property): "+fieldDeclaration.ToString()+" at "+fieldDeclaration.StartLocation.ToString());
							#endif
							data = true;
							
						}
						
					}
				}
				
				return base.TrackedVisit(variableDeclaration, data);
			}
			
			public override object TrackedVisit(AssignmentExpression assignmentExpression, object data)
			{
				if (this.FoundFileName == null &&	// skip if already found to improve performance
				    assignmentExpression.Op == AssignmentOperatorType.Assign && this.PositionAvailable &&
				    (!this.isLocalVariable || this.resourceManagerMember.Region.IsInside(this.CurrentNodeStartLocation.Y, this.CurrentNodeStartLocation.X))	// skip if local variable is out of scope
				   ) {
					
					IMember resolvedMember = null;
					ResolveResult rr = this.Resolve(assignmentExpression.Left, this.resourceManagerMember.DeclaringType.CompilationUnit.FileName);
					if (rr != null) {
						// Support both local variables and member variables
						MemberResolveResult mrr = rr as MemberResolveResult;
						if (mrr != null) {
							resolvedMember = mrr.ResolvedMember;
						} else {
							LocalResolveResult lrr = rr as LocalResolveResult;
							if (lrr != null) {
								resolvedMember = lrr.Field;
							}
						}
					}
					
					if (resolvedMember != null) {
						
						#if DEBUG
						LoggingService.Debug("ResourceToolkit: BclNRefactoryResourceResolver: Resolved member: "+resolvedMember.ToString());
						#endif
						
						// HACK: The GetType()s are necessary because the DOM IComparable implementations try to cast the parameter object to their own interface type which may fail.
						if (resolvedMember.GetType().Equals(this.resourceManagerMember.GetType()) && resolvedMember.CompareTo(this.resourceManagerMember) == 0) {
							
							#if DEBUG
							LoggingService.Debug("ResourceToolkit: BclNRefactoryResourceResolver found assignment to field: "+assignmentExpression.ToString());
							#endif
							data = true;
							
							// Resolving the property association only makes sense if
							// there is a possible relationship between the return types
							// of the resolved member and the member we are looking for.
						} else if (this.compilationUnit != null && !this.isLocalVariable &&
						           IsTypeRelationshipPossible(resolvedMember, this.resourceManagerMember)) {
							
							if (this.resourceManagerMember is IProperty && resolvedMember is IField) {
								// Find out if the resourceManagerMember is a property whose get block returns the value of the resolved member.
								
								// We might already have found this association in the
								// resourceManagerFieldAccessedByProperty field.
								this.TryResolveResourceManagerProperty();
								
								if (this.resourceManagerFieldAccessedByProperty != null && this.resourceManagerFieldAccessedByProperty.CompareTo(resolvedMember) == 0) {
									#if DEBUG
									LoggingService.Debug("ResourceToolkit: BclNRefactoryResourceResolver found assignment to field: "+assignmentExpression.ToString());
									#endif
									data = true;
								}
								
							} else if (this.resourceManagerMember is IField && resolvedMember is IProperty) {
								// Find out if the resolved member is a property whose set block assigns the value to the resourceManagerMember.
								
								PropertyFieldAssociationVisitor visitor = new PropertyFieldAssociationVisitor((IField)this.resourceManagerMember);
								this.compilationUnit.AcceptVisitor(visitor, null);
								if (visitor.AssociatedProperty != null && visitor.AssociatedProperty.CompareTo(resolvedMember) == 0) {
									#if DEBUG
									LoggingService.Debug("ResourceToolkit: BclNRefactoryResourceResolver found assignment to property: "+assignmentExpression.ToString());
									#endif
									data = true;
								}
								
							}
							
						}
						
					}
					
				}
				return base.TrackedVisit(assignmentExpression, data);
			}
			
			/// <summary>
			/// If the resourceManagerMember is a property, this method tries
			/// to find the field that this property is associated to.
			/// This association is cached in the
			/// resourceManagerFieldAccessedByProperty field
			/// to improve performance.
			/// </summary>
			void TryResolveResourceManagerProperty()
			{
				if (this.resourceManagerFieldAccessedByProperty == null && !this.triedToResolvePropertyAssociation) {
					
					// Don't try this more than once in the same CompilationUnit
					this.triedToResolvePropertyAssociation = true;
					
					IProperty prop = this.resourceManagerMember as IProperty;
					if (prop != null) {
						
						// Resolve the property association.
						PropertyFieldAssociationVisitor visitor = new PropertyFieldAssociationVisitor(prop);
						this.compilationUnit.AcceptVisitor(visitor, null);
						
						// Store the association in the instance field.
						this.resourceManagerFieldAccessedByProperty = visitor.AssociatedField;
						
					}
					
				}
			}
			
			public override object TrackedVisit(ObjectCreateExpression objectCreateExpression, object data)
			{
				if (data as bool? ?? false) {
					
					#if DEBUG
					LoggingService.Debug("ResourceToolkit: BclNRefactoryResourceResolver found object initialization: "+objectCreateExpression.ToString());
					#endif
					
					// Resolve the constructor.
					// A type derived from the declaration type is also allowed.
					MemberResolveResult mrr = this.Resolve(objectCreateExpression, this.resourceManagerMember.DeclaringType.CompilationUnit.FileName) as MemberResolveResult;
					
					#if DEBUG
					if (mrr != null) {
						LoggingService.Debug("ResourceToolkit: BclNRefactoryResourceResolver: resolved constructor: "+mrr.ResolvedMember.ToString());
					}
					#endif
					
					if (mrr != null &&
					    mrr.ResolvedMember is IMethod &&
					    (mrr.ResolvedMember.DeclaringType.CompareTo(this.resourceManagerMember.ReturnType.GetUnderlyingClass()) == 0 ||
					     mrr.ResolvedMember.DeclaringType.IsTypeInInheritanceTree(this.resourceManagerMember.ReturnType.GetUnderlyingClass()))
					   ) {
						
						// This most probably is the resource manager initialization we are looking for.
						// Find a parameter that indicates the resources being referenced.
						
						foreach (Expression param in objectCreateExpression.Parameters) {
							
							PrimitiveExpression p = param as PrimitiveExpression;
							if (p != null) {
								string pValue = p.Value as string;
								if (pValue != null) {
									
									#if DEBUG
									LoggingService.Debug("ResourceToolkit: BclNRefactoryResourceResolver found string parameter: '"+pValue+"'");
									#endif
									
									string fileName = NRefactoryResourceResolver.GetResourceFileNameByResourceName(this.resourceManagerMember.DeclaringType.CompilationUnit.FileName, pValue);
									if (fileName != null) {
										#if DEBUG
										LoggingService.Debug("ResourceToolkit: BclNRefactoryResourceResolver found resource file: "+fileName);
										#endif
										this.foundFileName = fileName;
										break;
									}
									
								}
								
								continue;
							}
							
							// Support typeof(...)
							TypeOfExpression t = param as TypeOfExpression;
							if (t != null && this.PositionAvailable) {
								TypeResolveResult trr = this.Resolve(new TypeReferenceExpression(t.TypeReference), this.resourceManagerMember.DeclaringType.CompilationUnit.FileName) as TypeResolveResult;
								if (trr != null) {
									
									#if DEBUG
									LoggingService.Debug("ResourceToolkit: BclNRefactoryResourceResolver found typeof(...) parameter, type: '"+trr.ResolvedType.ToString()+"'");
									#endif
									
									string fileName = NRefactoryResourceResolver.GetResourceFileNameByResourceName(this.resourceManagerMember.DeclaringType.CompilationUnit.FileName, trr.ResolvedType.FullyQualifiedName);
									if (fileName != null) {
										#if DEBUG
										LoggingService.Debug("ResourceToolkit: BclNRefactoryResourceResolver found resource file: "+fileName);
										#endif
										this.foundFileName = fileName;
										break;
									}
									
								}
							}
							
						}
						
					}
					
				}
				
				return base.TrackedVisit(objectCreateExpression, data);
			}
			
		}
		
		#endregion
		
		/// <summary>
		/// Determines whether there is a possible relationship between the
		/// return types of member1 and member2.
		/// </summary>
		public static bool IsTypeRelationshipPossible(IMember member1, IMember member2)
		{
			if (member1.ReturnType.Equals(member2.ReturnType)) {
				return true;
			}
			IClass class1;
			IClass class2;
			if ((class1 = member1.ReturnType.GetUnderlyingClass()) == null) {
				return false;
			}
			if ((class2 = member2.ReturnType.GetUnderlyingClass()) == null) {
				return false;
			}
			return class1.IsTypeInInheritanceTree(class2) ||
				class2.IsTypeInInheritanceTree(class1);
		}
		
		// ********************************************************************************************************************************
		
		/// <summary>
		/// Tries to infer the resource key being referenced from the given expression.
		/// </summary>
		static string GetKeyFromExpression(Expression expr)
		{
			#if DEBUG
			LoggingService.Debug("ResourceToolkit: BclNRefactoryResourceResolver trying to get key from expression: "+expr.ToString());
			#endif
			
			IndexerExpression indexer = expr as IndexerExpression;
			if (indexer != null) {
				foreach (Expression index in indexer.Indexes) {
					PrimitiveExpression p = index as PrimitiveExpression;
					if (p != null) {
						string key = p.Value as string;
						if (key != null) {
							#if DEBUG
							LoggingService.Debug("ResourceToolkit: BclNRefactoryResourceResolver found key: "+key);
							#endif
							return key;
						}
					}
				}
			}
			
			InvocationExpression invocation = expr as InvocationExpression;
			if (invocation != null) {
				FieldReferenceExpression fre = invocation.TargetObject as FieldReferenceExpression;
				if (fre != null) {
					if (fre.FieldName == "GetString" || fre.FieldName == "GetObject" || fre.FieldName == "GetStream") {
						if (invocation.Arguments.Count > 0) {
							PrimitiveExpression p = invocation.Arguments[0] as PrimitiveExpression;
							if (p != null) {
								string key = p.Value as string;
								if (key != null) {
									#if DEBUG
									LoggingService.Debug("ResourceToolkit: BclNRefactoryResourceResolver found key: "+key);
									#endif
									return key;
								}
							}
						}
					}
				}
			}
			
			return null;
		}
		
		// ********************************************************************************************************************************
		
		/// <summary>
		/// Gets a list of patterns that can be searched for in the specified file
		/// to find possible resource references that are supported by this
		/// resolver.
		/// </summary>
		/// <param name="fileName">The name of the file to get a list of possible patterns for.</param>
		public IEnumerable<string> GetPossiblePatternsForFile(string fileName)
		{
			return new string[] {
				"GetString",
				"GetObject",
				"GetStream",
				(NRefactoryResourceResolver.GetLanguagePropertiesForFile(fileName) ?? LanguageProperties.None).IndexerExpressionStartToken
			};
		}
		
		// ********************************************************************************************************************************
		
		/// <summary>
		/// Initializes a new instance of the <see cref="BclNRefactoryResourceResolver"/> class.
		/// </summary>
		public BclNRefactoryResourceResolver()
		{
		}
	}
}
